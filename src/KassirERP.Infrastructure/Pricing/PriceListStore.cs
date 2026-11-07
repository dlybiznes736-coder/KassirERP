using KassirERP.Application.Pricing;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Pricing;

/// <summary>Прайс-листы через TenantDb. Журнал цен пишут триггеры базы (миграция 0008).</summary>
internal sealed class PriceListStore(TenantDb db) : IPriceListStore
{
    public Task<PricingResult> CreateAsync(NewPriceList list, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            try
            {
                var inserted = await ExecAsync(connection, tx,
                    "INSERT INTO price_lists (id, tenant_id, name) VALUES ($1, current_tenant_id(), $2) ON CONFLICT (id) DO NOTHING",
                    ct, list.Id, list.Name);
                if (inserted == 1)
                {
                    return new PricingResult(PricingStatus.Done);
                }

                await using var existing = new NpgsqlCommand("SELECT name FROM price_lists WHERE id = $1", connection, tx);
                existing.Parameters.Add(new NpgsqlParameter { Value = list.Id });
                return await existing.ExecuteScalarAsync(ct) is string name && name == list.Name
                    ? new PricingResult(PricingStatus.AlreadyDone)
                    : new PricingResult(PricingStatus.Conflict, "Прайс-лист с таким id уже есть, и он другой.");
            }
            catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_price_lists_name" })
            {
                return new PricingResult(PricingStatus.Conflict, "Прайс-лист с таким названием уже есть.");
            }
        }, ct);

    public Task<IReadOnlyList<PriceListView>> ListAsync(CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<PriceListView>>([new NpgsqlBatchCommand(
            """
            SELECT l.id, l.name,
                   (SELECT count(*) FROM price_list_items i WHERE i.price_list_id = l.id),
                   coalesce((SELECT array_agg(s.id ORDER BY s.name) FROM stores s WHERE s.price_list_id = l.id), '{}')
            FROM price_lists l WHERE l.deleted_at IS NULL ORDER BY l.name
            """)], async reader =>
        {
            var lists = new List<PriceListView>();
            while (await reader.ReadAsync(ct))
            {
                lists.Add(new PriceListView(reader.GetGuid(0), reader.GetString(1), (int)reader.GetInt64(2), reader.GetFieldValue<Guid[]>(3)));
            }

            return lists;
        }, ct);

    public Task<IReadOnlyList<PriceListItemView>?> GetItemsAsync(Guid listId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<PriceListItemView>?>(
            [Batch("SELECT 1 FROM price_lists WHERE id = $1 AND deleted_at IS NULL", listId),
             Batch("SELECT variant_id, sale_price, updated_at FROM price_list_items WHERE price_list_id = $1 ORDER BY variant_id", listId)],
            async reader =>
            {
                if (!await reader.ReadAsync(ct))
                {
                    return null;
                }

                await reader.NextResultAsync(ct);
                var items = new List<PriceListItemView>();
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new PriceListItemView(reader.GetGuid(0), reader.GetDecimal(1), reader.GetFieldValue<DateTimeOffset>(2)));
                }

                return items;
            }, ct);

    public Task<PricingResult> SetPricesAsync(Guid listId, IReadOnlyList<PriceItem> items, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            if (!await ExistsAsync(connection, tx, "SELECT 1 FROM price_lists WHERE id = $1 AND deleted_at IS NULL", listId, ct))
            {
                return new PricingResult(PricingStatus.NotFound, "Прайс-лист не найден.");
            }

            // Пары — своей фирмы (видны под RLS); FK пропустил бы чужие.
            await using (var count = new NpgsqlCommand(
                "SELECT count(*) FROM product_variants WHERE id = ANY($1) AND deleted_at IS NULL", connection, tx))
            {
                count.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.VariantId).ToArray() });
                if ((long)(await count.ExecuteScalarAsync(ct))! != items.Count)
                {
                    return new PricingResult(PricingStatus.Invalid, "Пара не найдена.");
                }
            }

            // Одна вставка: новые цены — INSERT, изменённые — UPDATE (журнал — триггерами).
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO price_list_items (tenant_id, price_list_id, variant_id, sale_price)
                SELECT current_tenant_id(), $1, u.variant_id, u.price
                FROM unnest($2::uuid[], $3::numeric[]) AS u(variant_id, price)
                ON CONFLICT (price_list_id, variant_id) DO UPDATE SET sale_price = EXCLUDED.sale_price
                WHERE price_list_items.sale_price IS DISTINCT FROM EXCLUDED.sale_price
                """, connection, tx);
            upsert.Parameters.Add(new NpgsqlParameter { Value = listId });
            upsert.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.VariantId).ToArray() });
            upsert.Parameters.Add(new NpgsqlParameter { Value = items.Select(i => i.SalePrice).ToArray() });
            var changed = await upsert.ExecuteNonQueryAsync(ct);
            return new PricingResult(PricingStatus.Done, Body: new { changed });
        }, ct);

    public Task<PricingResult> RemovePriceAsync(Guid listId, Guid variantId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var removed = await ExecAsync(connection, tx,
                "DELETE FROM price_list_items WHERE price_list_id = $1 AND variant_id = $2", ct, listId, variantId);
            return new PricingResult(removed == 1 ? PricingStatus.Done : PricingStatus.NotFound, removed == 1 ? null : "Цены этой пары в листе нет.");
        }, ct);

    public Task<PricingResult> AssignToStoreAsync(Guid storeId, Guid? listId, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            if (listId is { } id && !await ExistsAsync(connection, tx, "SELECT 1 FROM price_lists WHERE id = $1 AND deleted_at IS NULL", id, ct))
            {
                return new PricingResult(PricingStatus.Invalid, "Прайс-лист не найден.");
            }

            await using var update = new NpgsqlCommand("UPDATE stores SET price_list_id = $2 WHERE id = $1", connection, tx);
            update.Parameters.Add(new NpgsqlParameter { Value = storeId });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)listId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            return await update.ExecuteNonQueryAsync(ct) == 1
                ? new PricingResult(PricingStatus.Done)
                : new PricingResult(PricingStatus.NotFound, "Филиал не найден.");
        }, ct);

    public Task<IReadOnlyList<PriceChangeView>> ChangesAsync(Guid? listId, Guid? variantId, CancellationToken ct)
    {
        var query = new NpgsqlBatchCommand(
            """
            SELECT price_list_id, variant_id, old_price, new_price, changed_by, changed_at FROM price_changes
            WHERE ($1::uuid IS NULL OR price_list_id = $1) AND ($2::uuid IS NULL OR variant_id = $2)
            ORDER BY changed_at DESC, id DESC
            LIMIT 500
            """)
        {
            Parameters =
            {
                new NpgsqlParameter { Value = (object?)listId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new NpgsqlParameter { Value = (object?)variantId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
            },
        };

        return db.ReadAsync<IReadOnlyList<PriceChangeView>>([query], async reader =>
        {
            var changes = new List<PriceChangeView>();
            while (await reader.ReadAsync(ct))
            {
                changes.Add(new PriceChangeView(
                    reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }

            return changes;
        }, ct);
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
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlBatchCommand Batch(string sql, Guid id) => new(sql) { Parameters = { new NpgsqlParameter { Value = id } } };
}
