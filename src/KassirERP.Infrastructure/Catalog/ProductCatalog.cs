using KassirERP.Application.Catalog;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Catalog;

/// <summary>
/// Товары. Все запросы идут через <see cref="TenantDb"/>; фильтр по тенанту
/// накладывает RLS. При записи tenant_id = current_tenant_id() в самом SQL —
/// значение из C# в запрос не передаётся вообще.
/// </summary>
internal sealed class ProductCatalog(TenantDb db) : IProductCatalog
{
    private const string ScanColumns =
        """
        SELECT v.id, p.id, p.name, p.brand, p.article, v.size, v.color, v.barcode, v.unit, v.plu,
               coalesce(pli.sale_price, v.sale_price), v.sale_price, pli.price_list_id,
               coalesce(s.qty, 0), v.barcode IS DISTINCT FROM $1 AS by_plu
        FROM product_variants v
        JOIN products p ON p.id = v.product_id
        LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = $2 AND s.variant_id = v.id
        -- Цена листа филиала (миграция 0008): два поиска по первичному ключу.
        LEFT JOIN stores st ON st.id = $2
        LEFT JOIN price_list_items pli ON pli.price_list_id = st.price_list_id AND pli.variant_id = v.id

        """;

    // Обычный скан — тот же запрос, что № 1 в db/bench/bench.sql (ux_variants_barcode + stock_pkey).
    private const string ScanSql = ScanColumns +
        """
        WHERE v.barcode = $1 AND v.deleted_at IS NULL AND p.deleted_at IS NULL
        """;

    // Код похож на весовой (2x, верная контрольная цифра): точный штрихкод или
    // пара по PLU ($3, ux_variants_plu), если префикс ($4) отдан весам. Точный
    // штрихкод важнее: товары с заводским кодом 2x продолжают сканироваться.
    private const string WeightScanSql = ScanColumns +
        """
        WHERE v.deleted_at IS NULL AND p.deleted_at IS NULL
          AND (v.barcode = $1
               OR (v.plu = $3 AND v.unit = 'kg'
                   AND (NOT EXISTS (SELECT 1 FROM weight_barcode_prefixes)
                        OR EXISTS (SELECT 1 FROM weight_barcode_prefixes w WHERE w.prefix = $4))))
        ORDER BY by_plu
        LIMIT 1
        """;

    // Поиск: сначала до $2 товаров (catalog.search_products — индекс
    // ix_products_search, миграция 0003), потом все пары этих товаров.
    // Пары читаются уже под RLS, как обычно.
    private const string SearchSql =
        """
        SELECT p.id, p.name, p.brand, p.article, v.id, v.size, v.color, v.barcode, v.unit,
               coalesce(pli.sale_price, v.sale_price)
        FROM catalog.search_products($1, $2) p
        JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
        LEFT JOIN stores st ON st.id = $3
        LEFT JOIN price_list_items pli ON pli.price_list_id = st.price_list_id AND pli.variant_id = v.id
        ORDER BY p.name, p.id, v.size, v.color
        """;

    private const string ProductSql =
        "SELECT id, category_id, name, brand, article FROM products WHERE id = $1 AND deleted_at IS NULL";

    private const string VariantsSql =
        """
        SELECT v.id, v.size, v.color, v.barcode, v.unit, v.plu, v.tracks_expiry,
               coalesce(pli.sale_price, v.sale_price), v.sale_price, pli.price_list_id, s.qty
        FROM product_variants v
        LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = $2 AND s.variant_id = v.id
        LEFT JOIN stores st ON st.id = $2
        LEFT JOIN price_list_items pli ON pli.price_list_id = st.price_list_id AND pli.variant_id = v.id
        WHERE v.product_id = $1 AND v.deleted_at IS NULL
        ORDER BY v.size, v.color
        """;

    private const string InsertProductSql =
        """
        INSERT INTO products (id, tenant_id, category_id, name, brand, article, description)
        VALUES ($1, current_tenant_id(), $2, $3, $4, $5, $6)
        """;

    private const string InsertVariantsSql =
        """
        INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode, cost_price, sale_price,
                                      unit, plu, tracks_expiry)
        SELECT u.id, current_tenant_id(), $1, u.size, u.color, u.barcode, u.cost, u.sale, u.unit, u.plu, u.tracks
        FROM unnest($2::uuid[], $3::text[], $4::text[], $5::text[], $6::numeric[], $7::numeric[],
                    $8::text[], $9::text[], $10::boolean[])
             AS u(id, size, color, barcode, cost, sale, unit, plu, tracks)
        """;

    public Task<ScanMatch?> FindByBarcodeAsync(string barcode, WeightBarcode? weight, Guid storeId, CancellationToken ct)
    {
        var query = weight is null
            ? Command(ScanSql, barcode, storeId)
            : Command(WeightScanSql, barcode, storeId, weight.Plu, weight.Prefix);
        return db.ReadAsync([query], async reader =>
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new ScanMatch(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
                NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), reader.GetString(8), NullableString(reader, 9),
                reader.GetDecimal(10), reader.GetDecimal(11), reader.IsDBNull(12) ? null : reader.GetGuid(12),
                reader.GetDecimal(13), reader.GetBoolean(14));
        }, ct);
    }

    public Task<IReadOnlyList<SearchProductView>> SearchByNameAsync(string query, int limit, Guid? storeId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<SearchProductView>>([Command(SearchSql, query, limit, (object?)storeId ?? DBNull.Value)], async reader =>
        {
            var products = new List<SearchProductView>();
            List<SearchVariantView>? variants = null;
            var currentId = Guid.Empty;
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.GetGuid(0);
                if (productId != currentId)
                {
                    currentId = productId;
                    variants = [];
                    products.Add(new SearchProductView(productId, reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), variants));
                }

                variants!.Add(new SearchVariantView(reader.GetGuid(4), NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7),
                    reader.GetString(8), reader.GetDecimal(9)));
            }

            return products;
        }, ct);

    public Task<ProductView?> GetAsync(Guid productId, Guid? storeId, CancellationToken ct) =>
        db.ReadAsync([Command(ProductSql, productId), Command(VariantsSql, productId, (object?)storeId ?? DBNull.Value)], async reader =>
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            var id = reader.GetGuid(0);
            Guid? categoryId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            var name = reader.GetString(2);
            var brand = NullableString(reader, 3);
            var article = NullableString(reader, 4);

            await reader.NextResultAsync(ct);
            var variants = new List<VariantView>();
            while (await reader.ReadAsync(ct))
            {
                decimal? stock = storeId is null ? null : reader.IsDBNull(10) ? 0m : reader.GetDecimal(10);
                variants.Add(new VariantView(reader.GetGuid(0), NullableString(reader, 1), NullableString(reader, 2), NullableString(reader, 3),
                    reader.GetString(4), NullableString(reader, 5), reader.GetBoolean(6), reader.GetDecimal(7), reader.GetDecimal(8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9), stock));
            }

            return new ProductView(id, categoryId, name, brand, article, variants);
        }, ct);

    public Task<WriteResult> CreateAsync(NewProduct product, CancellationToken ct) =>
        db.WriteAsync(async (connection, transaction) =>
        {
            if (product.CategoryId is { } categoryId && !await ExistsAsync(connection, transaction,
                    "SELECT 1 FROM categories WHERE id = $1 AND deleted_at IS NULL", categoryId, ct))
            {
                // FK проверяет существование мимо RLS, поэтому категорию другой
                // фирмы он бы пропустил. Проверяем видимость сами.
                return new WriteResult(WriteStatus.UnknownCategory, "Категория не найдена.");
            }

            try
            {
                await using (var insert = new NpgsqlCommand(InsertProductSql, connection, transaction))
                {
                    insert.Parameters.Add(new NpgsqlParameter { Value = product.Id });
                    insert.Parameters.Add(new NpgsqlParameter { Value = (object?)product.CategoryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
                    insert.Parameters.Add(new NpgsqlParameter { Value = product.Name.Trim() });
                    insert.Parameters.Add(TextOrNull(product.Brand));
                    insert.Parameters.Add(TextOrNull(product.Article));
                    insert.Parameters.Add(TextOrNull(product.Description));
                    await insert.ExecuteNonQueryAsync(ct);
                }

                await InsertVariantsAsync(connection, transaction, product.Id, product.Variants, ct);
                return new WriteResult(WriteStatus.Created);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Conflict(ex);
            }
        }, ct);

    public Task<WriteResult> AddVariantsAsync(Guid productId, IReadOnlyList<NewVariant> variants, CancellationToken ct) =>
        db.WriteAsync(async (connection, transaction) =>
        {
            if (!await ExistsAsync(connection, transaction,
                    "SELECT 1 FROM products WHERE id = $1 AND deleted_at IS NULL FOR SHARE", productId, ct))
            {
                return new WriteResult(WriteStatus.NotFound);
            }

            try
            {
                await InsertVariantsAsync(connection, transaction, productId, variants, ct);
                return new WriteResult(WriteStatus.Created);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Conflict(ex);
            }
        }, ct);

    private static async Task InsertVariantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid productId,
        IReadOnlyList<NewVariant> variants, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(InsertVariantsSql, connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = productId });
        insert.Parameters.Add(new NpgsqlParameter { Value = variants.Select(v => v.Id).ToArray() });
        // Значения уже нормализованы CatalogService: пустое — null.
        insert.Parameters.Add(TextArray(variants.Select(v => v.Size)));
        insert.Parameters.Add(TextArray(variants.Select(v => v.Color)));
        insert.Parameters.Add(TextArray(variants.Select(v => v.Barcode)));
        insert.Parameters.Add(new NpgsqlParameter { Value = variants.Select(v => v.CostPrice).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = variants.Select(v => v.SalePrice).ToArray() });
        insert.Parameters.Add(TextArray(variants.Select(v => (string?)v.Unit)));
        insert.Parameters.Add(TextArray(variants.Select(v => v.Plu)));
        insert.Parameters.Add(new NpgsqlParameter { Value = variants.Select(v => v.TracksExpiry).ToArray() });
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static WriteResult Conflict(PostgresException ex) => new(WriteStatus.Conflict, ex.ConstraintName switch
    {
        "products_pkey" => "Товар с таким id уже есть.",
        "product_variants_pkey" => "Пара с таким id уже есть.",
        "ux_variants_barcode" => "Штрихкод уже занят другой парой.",
        "ux_variants_product_size_color" => "У товара уже есть пара с таким размером и цветом (или пара без них).",
        "ux_variants_plu" => "PLU уже занят другим весовым товаром.",
        _ => "Запись уже существует.",
    });

    private static NpgsqlBatchCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg is DBNull
                ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }
                : new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static NpgsqlParameter TextArray(IEnumerable<string?> values) => new()
    {
        Value = values.ToArray(),
        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
    };

    private static NpgsqlParameter TextOrNull(string? value) => new()
    {
        Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(),
        NpgsqlDbType = NpgsqlDbType.Text,
    };

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
