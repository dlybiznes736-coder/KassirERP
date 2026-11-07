using KassirERP.Application.Catalog;
using KassirERP.Application.Transfers;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Transfers;

/// <summary>
/// Перемещения через TenantDb. Отправка и приём — UPDATE status; движения
/// склада (transfer_out / transfer_in) делает триггер trg_transfer_movements.
/// </summary>
internal sealed class TransferStore(TenantDb db) : ITransferStore
{
    private const string HeaderSql =
        "SELECT id, from_store_id, to_store_id, number, status, has_discrepancy, sent_at, received_at FROM transfers WHERE id = ANY($1) ORDER BY created_at, id";

    private const string ItemsSql =
        "SELECT transfer_id, id, variant_id, qty_sent, qty_received FROM transfer_items WHERE transfer_id = ANY($1) ORDER BY id";

    public Task<TransferResult> CreateAsync(NewTransfer transfer, Guid fromStoreId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            if (!await ExistsAsync(connection, tx, "SELECT 1 FROM stores WHERE id = $1 AND deleted_at IS NULL", transfer.ToStoreId, ct))
            {
                return new TransferResult(TransferStatus.Invalid, "Филиал-получатель не найден.");
            }

            var costs = new Dictionary<Guid, (string Unit, decimal Cost)>();
            await using (var variants = new NpgsqlCommand(
                "SELECT id, unit, cost_price FROM product_variants WHERE id = ANY($1) AND deleted_at IS NULL", connection, tx))
            {
                variants.Parameters.Add(new NpgsqlParameter { Value = transfer.Items.Select(i => i.VariantId).Distinct().ToArray() });
                await using var reader = await variants.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    costs[reader.GetGuid(0)] = (reader.GetString(1), reader.GetDecimal(2));
                }
            }

            foreach (var item in transfer.Items)
            {
                if (!costs.TryGetValue(item.VariantId, out var variant))
                {
                    return new TransferResult(TransferStatus.Invalid, $"Пара {item.VariantId} не найдена.");
                }

                if (variant.Unit == Units.Piece && item.Qty != decimal.Truncate(item.Qty))
                {
                    return new TransferResult(TransferStatus.Invalid, "Штучный товар перемещается целым числом.");
                }
            }

            var inserted = await ExecAsync(connection, tx,
                """
                INSERT INTO transfers (id, tenant_id, from_store_id, to_store_id, number, created_by)
                VALUES ($1, current_tenant_id(), $2, $3, $4, $5)
                ON CONFLICT (id) DO NOTHING
                """, ct, transfer.Id, fromStoreId, transfer.ToStoreId, (object?)transfer.Number ?? DBNull.Value, userId);
            if (inserted == 0)
            {
                // Повтор: тот же документ — 200, другой с тем же id (или чужой) — конфликт.
                var existing = await ReadAsync(connection, tx, transfer.Id, ct);
                return existing is not null && Same(existing, transfer, fromStoreId)
                    ? new TransferResult(TransferStatus.AlreadyDone, Transfer: existing)
                    : new TransferResult(TransferStatus.Conflict, "Перемещение с таким id уже есть, и оно другое.");
            }

            try
            {
                await using var items = new NpgsqlCommand(
                    """
                    INSERT INTO transfer_items (id, tenant_id, transfer_id, variant_id, qty_sent, cost_price)
                    SELECT u.id, current_tenant_id(), $1, u.variant_id, u.qty, u.cost
                    FROM unnest($2::uuid[], $3::uuid[], $4::numeric[], $5::numeric[]) AS u(id, variant_id, qty, cost)
                    """, connection, tx);
                items.Parameters.Add(new NpgsqlParameter { Value = transfer.Id });
                items.Parameters.Add(new NpgsqlParameter { Value = transfer.Items.Select(i => i.Id).ToArray() });
                items.Parameters.Add(new NpgsqlParameter { Value = transfer.Items.Select(i => i.VariantId).ToArray() });
                items.Parameters.Add(new NpgsqlParameter { Value = transfer.Items.Select(i => i.Qty).ToArray() });
                items.Parameters.Add(new NpgsqlParameter { Value = transfer.Items.Select(i => costs[i.VariantId].Cost).ToArray() });
                await items.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return new TransferResult(TransferStatus.Conflict, "Строка с таким id уже есть.");
            }

            return new TransferResult(TransferStatus.Done, Transfer: await ReadAsync(connection, tx, transfer.Id, ct));
        }, ct);

    public Task<TransferResult> SendAsync(Guid transferId, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var transfer = await LockAsync(connection, tx, transferId, ct);
            if (transfer is null)
            {
                return new TransferResult(TransferStatus.NotFound, "Перемещение не найдено.");
            }

            if (transfer.FromStoreId != storeId)
            {
                return new TransferResult(TransferStatus.WrongStore, "Отправляет филиал-отправитель.");
            }

            if (transfer.Status != "draft")
            {
                // Повторная отправка — не ошибка и не второе списание остатка.
                return new TransferResult(TransferStatus.AlreadyDone, Transfer: transfer);
            }

            // Триггер: transfer_out −qty в филиале-отправителе.
            await ExecAsync(connection, tx,
                "UPDATE transfers SET status = 'in_transit', sent_at = now(), sent_by = $2 WHERE id = $1", ct, transferId, userId);
            return new TransferResult(TransferStatus.Done, Transfer: await ReadAsync(connection, tx, transferId, ct));
        }, ct);

    public Task<TransferResult> ReceiveAsync(Guid transferId, IReadOnlyList<ReceivedItem> received, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var transfer = await LockAsync(connection, tx, transferId, ct);
            if (transfer is null)
            {
                return new TransferResult(TransferStatus.NotFound, "Перемещение не найдено.");
            }

            if (transfer.ToStoreId != storeId)
            {
                return new TransferResult(TransferStatus.WrongStore, "Принимает филиал-получатель.");
            }

            if (transfer.Status == "draft")
            {
                return new TransferResult(TransferStatus.WrongState, "Перемещение ещё не отправлено.");
            }

            var lines = transfer.Items.ToDictionary(i => i.Id);
            var unknown = received.FirstOrDefault(r => !lines.ContainsKey(r.Id));
            if (unknown is not null)
            {
                return new TransferResult(TransferStatus.Invalid, $"Строки {unknown.Id} нет в перемещении.");
            }

            var requested = received.ToDictionary(r => r.Id, r => r.QtyReceived);
            var over = transfer.Items.FirstOrDefault(i => requested.TryGetValue(i.Id, out var q) && q > i.QtySent);
            if (over is not null)
            {
                return new TransferResult(TransferStatus.Invalid,
                    $"Принято больше, чем отправлено: строка {over.Id}, отправлено {over.QtySent:0.###}.");
            }

            // Строки, которых нет в запросе, приняты полностью.
            var quantities = transfer.Items.ToDictionary(i => i.Id, i => requested.GetValueOrDefault(i.Id, i.QtySent));

            if (transfer.Status == "received")
            {
                // Повтор приёма: те же количества — 200, другие — конфликт (принятое не меняется).
                return transfer.Items.All(i => i.QtyReceived == quantities[i.Id])
                    ? new TransferResult(TransferStatus.AlreadyDone, Transfer: transfer)
                    : new TransferResult(TransferStatus.Conflict, "Перемещение уже принято с другими количествами.");
            }

            await using (var update = new NpgsqlCommand(
                """
                UPDATE transfer_items i SET qty_received = u.qty
                FROM unnest($2::uuid[], $3::numeric[]) AS u(id, qty)
                WHERE i.id = u.id AND i.transfer_id = $1
                """, connection, tx))
            {
                update.Parameters.Add(new NpgsqlParameter { Value = transferId });
                update.Parameters.Add(new NpgsqlParameter { Value = quantities.Keys.ToArray() });
                update.Parameters.Add(new NpgsqlParameter { Value = quantities.Values.ToArray() });
                await update.ExecuteNonQueryAsync(ct);
            }

            // Триггер: transfer_in +qty_received в филиале-получателе.
            var discrepancy = transfer.Items.Any(i => quantities[i.Id] < i.QtySent);
            await ExecAsync(connection, tx,
                "UPDATE transfers SET status = 'received', received_at = now(), received_by = $2, has_discrepancy = $3 WHERE id = $1",
                ct, transferId, userId, discrepancy);
            return new TransferResult(TransferStatus.Done, Transfer: await ReadAsync(connection, tx, transferId, ct));
        }, ct);

    public async Task<TransferView?> GetAsync(Guid transferId, CancellationToken ct) =>
        (await ReadManyAsync([transferId], ct)).FirstOrDefault();

    public Task<IReadOnlyList<TransferView>> IncomingAsync(Guid storeId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<TransferView>>(
            [
                Batch(HeaderSql.Replace("WHERE id = ANY($1)", "WHERE to_store_id = $1 AND status = 'in_transit'"), storeId),
                Batch(ItemsSql.Replace("WHERE transfer_id = ANY($1)",
                    "WHERE transfer_id IN (SELECT id FROM transfers WHERE to_store_id = $1 AND status = 'in_transit')"), storeId),
            ],
            reader => ReadDocumentsAsync(reader, ct), ct);

    // -----------------------------------------------------------------------

    private Task<IReadOnlyList<TransferView>> ReadManyAsync(Guid[] ids, CancellationToken ct) =>
        db.ReadAsync([Batch(HeaderSql, ids), Batch(ItemsSql, ids)], reader => ReadDocumentsAsync(reader, ct), ct);

    private static async Task<TransferView?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid transferId, CancellationToken ct)
    {
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM transfers WHERE id = $1 FOR UPDATE", connection, tx))
        {
            lockRow.Parameters.Add(new NpgsqlParameter { Value = transferId });
            if (await lockRow.ExecuteScalarAsync(ct) is null)
            {
                return null;
            }
        }

        return await ReadAsync(connection, tx, transferId, ct);
    }

    private static async Task<TransferView?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid transferId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, tx);
        batch.BatchCommands.Add(Batch(HeaderSql, new[] { transferId }));
        batch.BatchCommands.Add(Batch(ItemsSql, new[] { transferId }));
        await using var reader = await batch.ExecuteReaderAsync(ct);
        return (await ReadDocumentsAsync(reader, ct)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<TransferView>> ReadDocumentsAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var headers = new List<(Guid Id, Guid From, Guid To, string? Number, string Status, bool Discrepancy, DateTimeOffset? Sent, DateTimeOffset? Received)>();
        while (await reader.ReadAsync(ct))
        {
            headers.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        await reader.NextResultAsync(ct);
        var items = new Dictionary<Guid, List<TransferItemView>>();
        while (await reader.ReadAsync(ct))
        {
            var sent = reader.GetDecimal(3);
            decimal? got = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
            var list = items.TryGetValue(reader.GetGuid(0), out var l) ? l : items[reader.GetGuid(0)] = [];
            list.Add(new TransferItemView(reader.GetGuid(1), reader.GetGuid(2), sent, got, got is null ? null : sent - got));
        }

        return headers.Select(h => new TransferView(h.Id, h.From, h.To, h.Number, h.Status, h.Discrepancy, h.Sent, h.Received,
            items.GetValueOrDefault(h.Id) ?? [])).ToList();
    }

    private static bool Same(TransferView existing, NewTransfer request, Guid fromStoreId)
    {
        if (existing.FromStoreId != fromStoreId || existing.ToStoreId != request.ToStoreId || existing.Number != request.Number
            || existing.Items.Count != request.Items.Count)
        {
            return false;
        }

        var stored = existing.Items.ToDictionary(i => i.Id);
        return request.Items.All(i => stored.TryGetValue(i.Id, out var s) && s.VariantId == i.VariantId && s.QtySent == i.Qty);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg is DBNull
                ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text }
                : new NpgsqlParameter { Value = arg });
        }

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlBatchCommand Batch(string sql, object value) => new(sql) { Parameters = { new NpgsqlParameter { Value = value } } };
}
