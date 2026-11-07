using KassirERP.Application.Catalog;
using KassirERP.Application.Stock;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Stock;

/// <summary>
/// Списание и инвентаризация через TenantDb. Движения склада пишут триггеры
/// миграции 0010: writeoff — при вставке строк, inventory — при проведении.
/// </summary>
internal sealed class StockDocumentStore(TenantDb db) : IStockDocumentStore
{
    private const string WriteoffHeaderSql = "SELECT id, store_id, reason, note, created_at FROM writeoffs WHERE id = $1";
    private const string WriteoffItemsSql = "SELECT id, variant_id, qty, cost_price FROM writeoff_items WHERE writeoff_id = $1 ORDER BY id";

    private const string InventoryHeaderSql = "SELECT id, store_id, number, status, opened_at, posted_at FROM inventories WHERE id = $1";
    private const string InventoryItemsSql =
        "SELECT id, variant_id, counted_qty, book_qty, counted_at FROM inventory_items WHERE inventory_id = $1 ORDER BY counted_at, id";

    // --- Списание -----------------------------------------------------------

    public Task<WriteoffResult> CreateWriteoffAsync(NewWriteoff writeoff, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var variants = await VariantsAsync(connection, tx, writeoff.Items.Select(i => i.VariantId), ct);
            foreach (var item in writeoff.Items)
            {
                if (!variants.TryGetValue(item.VariantId, out var variant))
                {
                    return new WriteoffResult(StockDocStatus.Invalid, $"Пара {item.VariantId} не найдена.");
                }

                if (variant.Unit == Units.Piece && item.Qty != decimal.Truncate(item.Qty))
                {
                    return new WriteoffResult(StockDocStatus.Invalid, "Штучный товар списывается целым числом.");
                }
            }

            var inserted = await ExecAsync(connection, tx,
                """
                INSERT INTO writeoffs (id, tenant_id, store_id, reason, note, created_by)
                VALUES ($1, current_tenant_id(), $2, $3, $4, $5)
                ON CONFLICT (id) DO NOTHING
                """, ct, writeoff.Id, storeId, writeoff.Reason, (object?)writeoff.Note ?? DBNull.Value, userId);
            if (inserted == 0)
            {
                var existing = await ReadWriteoffAsync(connection, tx, writeoff.Id, ct);
                return existing is not null && Same(existing, writeoff, storeId)
                    ? new WriteoffResult(StockDocStatus.AlreadyDone, Writeoff: existing)
                    : new WriteoffResult(StockDocStatus.Conflict, "Списание с таким id уже есть, и оно другое.");
            }

            try
            {
                // Одним оператором: триггер пишет движения writeoff −qty (одно обновление остатка на пару).
                await using var items = new NpgsqlCommand(
                    """
                    INSERT INTO writeoff_items (id, tenant_id, writeoff_id, variant_id, qty, cost_price)
                    SELECT u.id, current_tenant_id(), $1, u.variant_id, u.qty, u.cost
                    FROM unnest($2::uuid[], $3::uuid[], $4::numeric[], $5::numeric[]) AS u(id, variant_id, qty, cost)
                    """, connection, tx);
                items.Parameters.Add(new NpgsqlParameter { Value = writeoff.Id });
                items.Parameters.Add(new NpgsqlParameter { Value = writeoff.Items.Select(i => i.Id).ToArray() });
                items.Parameters.Add(new NpgsqlParameter { Value = writeoff.Items.Select(i => i.VariantId).ToArray() });
                items.Parameters.Add(new NpgsqlParameter { Value = writeoff.Items.Select(i => i.Qty).ToArray() });
                items.Parameters.Add(new NpgsqlParameter { Value = writeoff.Items.Select(i => variants[i.VariantId].Cost).ToArray() });
                await items.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return new WriteoffResult(StockDocStatus.Conflict, "Строка с таким id уже есть.");
            }

            return new WriteoffResult(StockDocStatus.Done, Writeoff: await ReadWriteoffAsync(connection, tx, writeoff.Id, ct));
        }, ct);

    public Task<WriteoffView?> GetWriteoffAsync(Guid id, CancellationToken ct) =>
        db.ReadAsync([Batch(WriteoffHeaderSql, id), Batch(WriteoffItemsSql, id)], reader => ReadWriteoffAsync(reader, ct), ct);

    // --- Инвентаризация -----------------------------------------------------

    public Task<InventoryResult> OpenInventoryAsync(OpenInventory inventory, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            int inserted;
            try
            {
                inserted = await ExecAsync(connection, tx,
                    """
                    INSERT INTO inventories (id, tenant_id, store_id, number, opened_by)
                    VALUES ($1, current_tenant_id(), $2, $3, $4)
                    ON CONFLICT (id) DO NOTHING
                    """, ct, inventory.Id, storeId, (object?)inventory.Number ?? DBNull.Value, userId);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // ux_inventories_open_per_store: пересчёты в одном филиале не пересекаются.
                return new InventoryResult(StockDocStatus.Conflict, "В филиале уже открыта другая инвентаризация.");
            }

            var current = await ReadInventoryAsync(connection, tx, inventory.Id, ct);
            if (inserted == 0)
            {
                return current is not null && current.StoreId == storeId && current.Number == inventory.Number
                    ? new InventoryResult(StockDocStatus.AlreadyDone, Inventory: current)
                    : new InventoryResult(StockDocStatus.Conflict, "Инвентаризация с таким id уже есть, и она другая.");
            }

            return new InventoryResult(StockDocStatus.Done, Inventory: current);
        }, ct);

    public Task<InventoryResult> CountAsync(Guid inventoryId, IReadOnlyList<InventoryCount> items, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            // FOR SHARE: проведение (FOR UPDATE) ждёт конца подсчёта и видит его строки;
            // подсчёт, ждавший проведения, увидит status = posted и получит отказ.
            var (found, store, status) = await LockInventoryAsync(connection, tx, inventoryId, "FOR SHARE", ct);
            if (!found)
            {
                return new InventoryResult(StockDocStatus.NotFound, "Инвентаризация не найдена.");
            }

            if (store != storeId)
            {
                return new InventoryResult(StockDocStatus.WrongStore, "Инвентаризация другого филиала.");
            }

            if (status != "open")
            {
                return new InventoryResult(StockDocStatus.WrongState, "Инвентаризация проведена: подсчёт не принимается.");
            }

            var variants = await VariantsAsync(connection, tx, items.Select(i => i.VariantId), ct);
            foreach (var item in items)
            {
                if (!variants.TryGetValue(item.VariantId, out var variant))
                {
                    return new InventoryResult(StockDocStatus.Invalid, $"Пара {item.VariantId} не найдена.");
                }

                if (variant.Unit == Units.Piece && item.CountedQty != decimal.Truncate(item.CountedQty))
                {
                    return new InventoryResult(StockDocStatus.Invalid, "Штучный товар считается целым числом.");
                }
            }

            // Учётный остаток берётся тем же оператором, что пишет посчитанное:
            // разница counted − book относится ровно к этому моменту. Продажи после
            // него уменьшают остаток своими движениями и разницу не меняют.
            await using var count = new NpgsqlCommand(
                """
                INSERT INTO inventory_items (id, tenant_id, inventory_id, variant_id, counted_qty, book_qty, cost_price, counted_by, counted_at)
                SELECT uuid_v7(), current_tenant_id(), $1, u.variant_id, u.qty, coalesce(s.qty, 0), v.cost_price, $4, clock_timestamp()
                FROM unnest($2::uuid[], $3::numeric[]) AS u(variant_id, qty)
                JOIN product_variants v ON v.id = u.variant_id
                LEFT JOIN stock s ON s.store_id = $5 AND s.variant_id = u.variant_id
                ON CONFLICT (inventory_id, variant_id) DO UPDATE
                   SET counted_qty = EXCLUDED.counted_qty, book_qty = EXCLUDED.book_qty, cost_price = EXCLUDED.cost_price,
                       counted_by = EXCLUDED.counted_by, counted_at = EXCLUDED.counted_at
                """, connection, tx);
            count.Parameters.Add(new NpgsqlParameter { Value = inventoryId });
            count.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.VariantId).ToArray() });
            count.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.CountedQty).ToArray() });
            count.Parameters.Add(new NpgsqlParameter { Value = userId });
            count.Parameters.Add(new NpgsqlParameter { Value = storeId });
            await count.ExecuteNonQueryAsync(ct);

            return new InventoryResult(StockDocStatus.Done, Inventory: await ReadInventoryAsync(connection, tx, inventoryId, ct));
        }, ct);

    public Task<InventoryResult> PostInventoryAsync(Guid inventoryId, Guid storeId, Guid userId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var (found, store, status) = await LockInventoryAsync(connection, tx, inventoryId, "FOR UPDATE", ct);
            if (!found)
            {
                return new InventoryResult(StockDocStatus.NotFound, "Инвентаризация не найдена.");
            }

            if (store != storeId)
            {
                return new InventoryResult(StockDocStatus.WrongStore, "Инвентаризация другого филиала.");
            }

            if (status == "posted")
            {
                // Повтор проведения — 200 без второго движения.
                return new InventoryResult(StockDocStatus.AlreadyDone, Inventory: await ReadInventoryAsync(connection, tx, inventoryId, ct));
            }

            // Триггер: движение inventory на counted − book по каждой пересчитанной паре.
            await ExecAsync(connection, tx,
                "UPDATE inventories SET status = 'posted', posted_at = now(), posted_by = $2 WHERE id = $1", ct, inventoryId, userId);
            return new InventoryResult(StockDocStatus.Done, Inventory: await ReadInventoryAsync(connection, tx, inventoryId, ct));
        }, ct);

    public Task<InventoryView?> GetInventoryAsync(Guid id, CancellationToken ct) =>
        db.ReadAsync([Batch(InventoryHeaderSql, id), Batch(InventoryItemsSql, id)], reader => ReadInventoryAsync(reader, ct), ct);

    // -----------------------------------------------------------------------

    private static async Task<Dictionary<Guid, (string Unit, decimal Cost)>> VariantsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (string Unit, decimal Cost)>();
        await using var command = new NpgsqlCommand(
            "SELECT id, unit, cost_price FROM product_variants WHERE id = ANY($1) AND deleted_at IS NULL", connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = ids.Distinct().ToArray() });
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = (reader.GetString(1), reader.GetDecimal(2));
        }

        return result;
    }

    private static async Task<(bool Found, Guid Store, string Status)> LockInventoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, string lockClause, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT store_id, status FROM inventories WHERE id = $1 {lockClause}", connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (true, reader.GetGuid(0), reader.GetString(1)) : (false, Guid.Empty, "");
    }

    private static async Task<WriteoffView?> ReadWriteoffAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, tx);
        batch.BatchCommands.Add(Batch(WriteoffHeaderSql, id));
        batch.BatchCommands.Add(Batch(WriteoffItemsSql, id));
        await using var reader = await batch.ExecuteReaderAsync(ct);
        return await ReadWriteoffAsync(reader, ct);
    }

    private static async Task<WriteoffView?> ReadWriteoffAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var (id, store, reason, note, created) = (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
        await reader.NextResultAsync(ct);
        var items = new List<WriteoffItemView>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new WriteoffItemView(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetDecimal(3)));
        }

        return new WriteoffView(id, store, reason, note, created, items);
    }

    private static async Task<InventoryView?> ReadInventoryAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, tx);
        batch.BatchCommands.Add(Batch(InventoryHeaderSql, id));
        batch.BatchCommands.Add(Batch(InventoryItemsSql, id));
        await using var reader = await batch.ExecuteReaderAsync(ct);
        return await ReadInventoryAsync(reader, ct);
    }

    private static async Task<InventoryView?> ReadInventoryAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var (id, store, number, status, opened, posted) = (reader.GetGuid(0), reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5));
        await reader.NextResultAsync(ct);
        var items = new List<InventoryItemView>();
        while (await reader.ReadAsync(ct))
        {
            var counted = reader.GetDecimal(2);
            var book = reader.GetDecimal(3);
            items.Add(new InventoryItemView(reader.GetGuid(0), reader.GetGuid(1), counted, book, counted - book,
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return new InventoryView(id, store, number, status, opened, posted, items);
    }

    private static bool Same(WriteoffView existing, NewWriteoff request, Guid storeId)
    {
        if (existing.StoreId != storeId || existing.Reason != request.Reason || existing.Note != request.Note
            || existing.Items.Count != request.Items.Count)
        {
            return false;
        }

        var stored = existing.Items.ToDictionary(i => i.Id);
        return request.Items.All(i => stored.TryGetValue(i.Id, out var s) && s.VariantId == i.VariantId && s.Qty == i.Qty);
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
