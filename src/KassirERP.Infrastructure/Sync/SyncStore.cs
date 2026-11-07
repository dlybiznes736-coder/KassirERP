using KassirERP.Application.Sync;
using KassirERP.Infrastructure.Persistence;
using Npgsql;

namespace KassirERP.Infrastructure.Sync;

/// <summary>
/// Изменения для кассы по курсору (момент изменения, id). Отдаёт только строки
/// старше горизонта sync_horizon() (миграция 0011): всё, что старше, уже
/// закоммичено, и курсор не перепрыгнет строку долгой транзакции.
/// Горизонт — отдельным оператором ДО чтения: в READ COMMITTED у следующего
/// оператора свежий снимок, и в нём видны все транзакции, начатые до горизонта.
/// </summary>
internal sealed class SyncStore(TenantDb db) : ISyncStore
{
    private const string ProductsSql =
        """
        SELECT id, name, category_id, is_active, deleted_at IS NOT NULL, updated_at
        FROM products
        WHERE updated_at < $1 AND (updated_at, id) > ($2, $3)
        ORDER BY updated_at, id
        LIMIT $4
        """;

    private const string VariantsSql =
        """
        SELECT id, product_id, size, color, barcode, unit, plu, sale_price, tracks_expiry, is_active, deleted_at IS NOT NULL, updated_at
        FROM product_variants
        WHERE updated_at < $1 AND (updated_at, id) > ($2, $3)
        ORDER BY updated_at, id
        LIMIT $4
        """;

    // Цена в листе (вид 1) и снятие цены из листа (вид 0, из журнала). При
    // равных (момент, пара) снятие идёт раньше цены: удалили и снова добавили
    // в одной транзакции — у кассы остаётся цена.
    private const string PricesSql =
        """
        SELECT at, variant_id, kind, sale_price FROM (
          (SELECT updated_at AS at, variant_id, 1 AS kind, sale_price
           FROM price_list_items
           WHERE price_list_id = $5 AND updated_at < $1 AND (updated_at, variant_id) >= ($2, $3)
             AND (updated_at, variant_id, 1) > ($2, $3, $6)
           ORDER BY updated_at, variant_id LIMIT $4)
          UNION ALL
          (SELECT changed_at, variant_id, 0, NULL::numeric
           FROM price_changes
           WHERE price_list_id = $5 AND new_price IS NULL AND changed_at < $1 AND (changed_at, variant_id) >= ($2, $3)
             AND (changed_at, variant_id, 0) > ($2, $3, $6)
           ORDER BY changed_at, variant_id LIMIT $4)
        ) c
        ORDER BY at, variant_id, kind
        LIMIT $4
        """;

    public Task<(IReadOnlyList<ProductChange> Items, bool HasMore)> ProductsAsync(SyncCursor after, int limit, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var horizon = await HorizonAsync(connection, tx, ct);
            var items = new List<ProductChange>();
            await using var command = Page(ProductsSql, connection, tx, horizon, after, limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new ProductChange(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }

            return Trim(items, limit);
        }, ct);

    public Task<(IReadOnlyList<VariantChange> Items, bool HasMore)> VariantsAsync(SyncCursor after, int limit, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var horizon = await HorizonAsync(connection, tx, ct);
            var items = new List<VariantChange>();
            await using var command = Page(VariantsSql, connection, tx, horizon, after, limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new VariantChange(reader.GetGuid(0), reader.GetGuid(1), Text(reader, 2), Text(reader, 3), Text(reader, 4),
                    reader.GetString(5), Text(reader, 6), reader.GetDecimal(7), reader.GetBoolean(8), reader.GetBoolean(9),
                    reader.GetBoolean(10), reader.GetFieldValue<DateTimeOffset>(11)));
            }

            return Trim(items, limit);
        }, ct);

    public Task<(Guid? PriceListId, bool Reset, IReadOnlyList<PriceChange> Items, SyncCursor Next, bool HasMore)> PricesAsync(
        Guid storeId, SyncCursor after, int limit, CancellationToken ct) =>
        db.WriteAsync<(Guid?, bool, IReadOnlyList<PriceChange>, SyncCursor, bool)>(async (connection, tx) =>
        {
            var horizon = await HorizonAsync(connection, tx, ct);
            Guid? listId;
            await using (var list = new NpgsqlCommand("SELECT price_list_id FROM stores WHERE id = $1", connection, tx))
            {
                list.Parameters.Add(new NpgsqlParameter { Value = storeId });
                listId = await list.ExecuteScalarAsync(ct) is Guid id ? id : null;
            }

            var (start, reset) = after.ForList(listId);
            if (listId is null)
            {
                // Без листа — базовые цены (они приходят с парами).
                return (null, reset, [], start, false);
            }

            var rows = new List<(PriceChange Change, int Kind)>();
            await using (var command = Page(PricesSql, connection, tx, horizon, start, limit + 1))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = listId.Value });
                command.Parameters.Add(new NpgsqlParameter { Value = start.Kind });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((new PriceChange(reader.GetGuid(1), reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                        reader.GetFieldValue<DateTimeOffset>(0)), reader.GetInt32(2)));
                }
            }

            var hasMore = rows.Count > limit;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var next = rows.Count == 0 ? start : start with { At = rows[^1].Change.ChangedAt, Id = rows[^1].Change.VariantId, Kind = rows[^1].Kind };
            return (listId, reset, rows.Select(r => r.Change).ToList(), next, hasMore);
        }, ct);

    // -----------------------------------------------------------------------

    private static async Task<DateTimeOffset> HorizonAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT sync_horizon()", connection, tx);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static NpgsqlCommand Page(string sql, NpgsqlConnection connection, NpgsqlTransaction tx, DateTimeOffset horizon, SyncCursor after, int limit)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = horizon });
        command.Parameters.Add(new NpgsqlParameter { Value = after.At });
        command.Parameters.Add(new NpgsqlParameter { Value = after.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        return command;
    }

    private static (IReadOnlyList<T> Items, bool HasMore) Trim<T>(List<T> items, int limit)
    {
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return (items, hasMore);
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
