using KassirERP.Application.Purchases;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Purchases;

/// <summary>
/// Накладные прихода через TenantDb. tenant_id в SQL — current_tenant_id().
/// Проведение — UPDATE status; движения склада и остатки делает триггер
/// trg_purchase_accept (одно обновление stock на пару, срок годности в движении).
/// </summary>
internal sealed class PurchaseStore(TenantDb db) : IPurchaseStore
{
    private const string HeaderSql =
        "SELECT id, store_id, supplier_id, number, doc_date, status, total, accepted_at FROM purchases WHERE id = $1";

    private const string ItemsSql =
        "SELECT id, variant_id, qty, cost_price, expires_on FROM purchase_items WHERE purchase_id = $1 ORDER BY id";

    public Task<PurchaseResult> CreateAsync(NewPurchase purchase, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            if (await CheckReferencesAsync(connection, tx, purchase.SupplierId, purchase.Items, ct) is { } refused)
            {
                return refused;
            }

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO purchases (id, tenant_id, store_id, supplier_id, number, doc_date, total, created_by)
                VALUES ($1, current_tenant_id(), $2, $3, $4, $5, $6, $7)
                ON CONFLICT (id) DO NOTHING
                """, connection, tx))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = purchase.Id });
                insert.Parameters.Add(new NpgsqlParameter { Value = storeId });
                insert.Parameters.Add(new NpgsqlParameter { Value = (object?)purchase.SupplierId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
                insert.Parameters.Add(new NpgsqlParameter { Value = (object?)purchase.Number ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                insert.Parameters.Add(new NpgsqlParameter { Value = purchase.DocDate!.Value });
                insert.Parameters.Add(new NpgsqlParameter { Value = Total(purchase.Items) });
                insert.Parameters.Add(new NpgsqlParameter { Value = userId });

                if (await insert.ExecuteNonQueryAsync(ct) == 0)
                {
                    // id уже есть. Тот же документ — повтор запроса (не дубль, 200);
                    // другое содержимое или чужая фирма (под RLS не видна) — конфликт.
                    var existing = await ReadAsync(connection, tx, purchase.Id, ct);
                    return existing is not null && SameDocument(existing, purchase, storeId)
                        ? new PurchaseResult(PurchaseStatus.AlreadyDone, Purchase: existing)
                        : new PurchaseResult(PurchaseStatus.Conflict, "Накладная с таким id уже есть, и она другая.");
                }
            }

            try
            {
                await InsertItemsAsync(connection, tx, purchase.Id, purchase.Items, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // id строки занят (в т.ч. строкой другой накладной).
                return new PurchaseResult(PurchaseStatus.Conflict, "Строка с таким id уже есть.");
            }

            return new PurchaseResult(PurchaseStatus.Done, Purchase: await ReadAsync(connection, tx, purchase.Id, ct));
        }, ct);

    public Task<PurchaseResult> ReplaceItemsAsync(Guid purchaseId, IReadOnlyList<NewPurchaseItem> items, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            // FOR UPDATE: правка строк и проведение одной накладной идут строго по очереди,
            // иначе строки могли бы добавиться к уже проведённой накладной без движений.
            if (await LockStatusAsync(connection, tx, purchaseId, ct) is not { } status)
            {
                return new PurchaseResult(PurchaseStatus.NotFound);
            }

            if (status == "accepted")
            {
                return Locked();
            }

            if (await CheckReferencesAsync(connection, tx, supplierId: null, items, ct) is { } refused)
            {
                return refused;
            }

            await ExecAsync(connection, tx, "DELETE FROM purchase_items WHERE purchase_id = $1", ct, purchaseId);
            try
            {
                await InsertItemsAsync(connection, tx, purchaseId, items, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return new PurchaseResult(PurchaseStatus.Conflict, "Строка с таким id уже есть в другой накладной.");
            }

            await ExecAsync(connection, tx, "UPDATE purchases SET total = $2 WHERE id = $1", ct, purchaseId, Total(items));
            return new PurchaseResult(PurchaseStatus.Done, Purchase: await ReadAsync(connection, tx, purchaseId, ct));
        }, ct);

    public Task<PurchaseResult> DeleteAsync(Guid purchaseId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            if (await LockStatusAsync(connection, tx, purchaseId, ct) is not { } status)
            {
                return new PurchaseResult(PurchaseStatus.NotFound);
            }

            if (status == "accepted")
            {
                return Locked();
            }

            await ExecAsync(connection, tx, "DELETE FROM purchase_items WHERE purchase_id = $1", ct, purchaseId);
            await ExecAsync(connection, tx, "DELETE FROM purchases WHERE id = $1", ct, purchaseId);
            return new PurchaseResult(PurchaseStatus.Done);
        }, ct);

    public Task<PurchaseResult> AcceptAsync(Guid purchaseId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            if (await LockStatusAsync(connection, tx, purchaseId, ct) is not { } status)
            {
                return new PurchaseResult(PurchaseStatus.NotFound);
            }

            if (status == "accepted")
            {
                // Повторное проведение — не ошибка и не второе движение склада.
                return new PurchaseResult(PurchaseStatus.AlreadyDone, Purchase: await ReadAsync(connection, tx, purchaseId, ct));
            }

            await using (var count = new NpgsqlCommand("SELECT count(*) FROM purchase_items WHERE purchase_id = $1", connection, tx))
            {
                count.Parameters.Add(new NpgsqlParameter { Value = purchaseId });
                if ((long)(await count.ExecuteScalarAsync(ct))! == 0)
                {
                    return new PurchaseResult(PurchaseStatus.Empty, "В накладной нет строк — нечего проводить.");
                }
            }

            // Триггер: движения склада (со сроком годности) и сдвиг остатков.
            await ExecAsync(connection, tx, "UPDATE purchases SET status = 'accepted', accepted_by = $2 WHERE id = $1", ct, purchaseId, userId);
            return new PurchaseResult(PurchaseStatus.Done, Purchase: await ReadAsync(connection, tx, purchaseId, ct));
        }, ct);

    public Task<PurchaseView?> GetAsync(Guid purchaseId, CancellationToken ct) =>
        db.ReadAsync([Batch(HeaderSql, purchaseId), Batch(ItemsSql, purchaseId)], reader => ReadDocumentAsync(reader, ct), ct);

    // -----------------------------------------------------------------------

    /// <summary>
    /// Поставщик и пары должны быть видны в своей фирме (RLS): FK проверяет только
    /// существование и пропустил бы чужие. Заодно — срок годности для пар с учётом сроков.
    /// </summary>
    private static async Task<PurchaseResult?> CheckReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid? supplierId, IReadOnlyList<NewPurchaseItem> items, CancellationToken ct)
    {
        if (supplierId is { } supplier)
        {
            await using var check = new NpgsqlCommand("SELECT 1 FROM suppliers WHERE id = $1 AND deleted_at IS NULL", connection, tx);
            check.Parameters.Add(new NpgsqlParameter { Value = supplier });
            if (await check.ExecuteScalarAsync(ct) is null)
            {
                return new PurchaseResult(PurchaseStatus.UnknownReference, "Поставщик не найден.");
            }
        }

        var ids = items.Select(i => i.VariantId).Distinct().ToArray();
        var tracked = new HashSet<Guid>();
        var found = 0;
        await using (var variants = new NpgsqlCommand(
            "SELECT id, tracks_expiry FROM product_variants WHERE id = ANY($1) AND deleted_at IS NULL", connection, tx))
        {
            variants.Parameters.Add(new NpgsqlParameter { Value = ids });
            await using var reader = await variants.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                found++;
                if (reader.GetBoolean(1))
                {
                    tracked.Add(reader.GetGuid(0));
                }
            }
        }

        if (found != ids.Length)
        {
            return new PurchaseResult(PurchaseStatus.UnknownReference, "Пара не найдена.");
        }

        var missingExpiry = items.Where(i => tracked.Contains(i.VariantId) && i.ExpiresOn is null).Select(i => i.Id).ToList();
        return missingExpiry.Count == 0
            ? null
            : new PurchaseResult(PurchaseStatus.ExpiryRequired,
                $"У товара включён учёт сроков — укажите срок годности в строках: {string.Join(", ", missingExpiry)}.");
    }

    private static async Task InsertItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid purchaseId,
        IReadOnlyList<NewPurchaseItem> items, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO purchase_items (id, tenant_id, purchase_id, variant_id, qty, cost_price, expires_on)
            SELECT u.id, current_tenant_id(), $1, u.variant_id, u.qty, u.cost, u.expires
            FROM unnest($2::uuid[], $3::uuid[], $4::numeric[], $5::numeric[], $6::date[]) AS u(id, variant_id, qty, cost, expires)
            """, connection, tx);
        insert.Parameters.Add(new NpgsqlParameter { Value = purchaseId });
        insert.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.Id).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.VariantId).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.Qty).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.CostPrice).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter
        {
            Value = items.Select(i => i.ExpiresOn).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Date,
        });
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> LockStatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid purchaseId, CancellationToken ct)
    {
        await using var select = new NpgsqlCommand("SELECT status FROM purchases WHERE id = $1 FOR UPDATE", connection, tx);
        select.Parameters.Add(new NpgsqlParameter { Value = purchaseId });
        return (string?)await select.ExecuteScalarAsync(ct);
    }

    private static async Task<PurchaseView?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid purchaseId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, tx);
        batch.BatchCommands.Add(Batch(HeaderSql, purchaseId));
        batch.BatchCommands.Add(Batch(ItemsSql, purchaseId));
        await using var reader = await batch.ExecuteReaderAsync(ct);
        return await ReadDocumentAsync(reader, ct);
    }

    private static async Task<PurchaseView?> ReadDocumentAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var id = reader.GetGuid(0);
        var store = reader.GetGuid(1);
        Guid? supplier = reader.IsDBNull(2) ? null : reader.GetGuid(2);
        var number = reader.IsDBNull(3) ? null : reader.GetString(3);
        var docDate = reader.GetFieldValue<DateOnly>(4);
        var status = reader.GetString(5);
        var total = reader.GetDecimal(6);
        DateTimeOffset? acceptedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7);

        await reader.NextResultAsync(ct);
        var items = new List<PurchaseItemView>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new PurchaseItemView(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4)));
        }

        return new PurchaseView(id, store, supplier, number, docDate, status, total, acceptedAt, items);
    }

    private static bool SameDocument(PurchaseView existing, NewPurchase request, Guid storeId)
    {
        if (existing.StoreId != storeId || existing.SupplierId != request.SupplierId || existing.Number != request.Number
            || existing.DocDate != request.DocDate || existing.Items.Count != request.Items.Count)
        {
            return false;
        }

        var stored = existing.Items.ToDictionary(i => i.Id);
        return request.Items.All(i => stored.TryGetValue(i.Id, out var s)
            && s.VariantId == i.VariantId && s.Qty == i.Qty && s.CostPrice == i.CostPrice && s.ExpiresOn == i.ExpiresOn);
    }

    private static decimal Total(IReadOnlyList<NewPurchaseItem> items) =>
        decimal.Round(items.Sum(i => i.Qty * i.CostPrice), 2, MidpointRounding.AwayFromZero);

    private static PurchaseResult Locked() =>
        new(PurchaseStatus.Locked, "Накладная проведена: менять и удалять её нельзя. Ошибку исправляет новый документ.");

    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlBatchCommand Batch(string sql, Guid id) =>
        new(sql) { Parameters = { new NpgsqlParameter { Value = id } } };
}
