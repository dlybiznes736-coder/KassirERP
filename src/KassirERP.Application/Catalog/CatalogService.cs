using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Catalog;

public sealed class CatalogValidationException(string message) : Exception(message);

/// <summary>Товары: скан, поиск, карточка, заведение моделей и пар.</summary>
public sealed class CatalogService(IProductCatalog catalog, ITenantContext tenant)
{
    public const int SearchLimit = 50;
    public const int MaxVariantsPerRequest = 200;

    /// <summary>Скан на кассе. Остаток — по филиалу из токена; без филиала скан невозможен.</summary>
    public Task<ScanView?> ScanAsync(string barcode, CancellationToken ct)
    {
        var code = barcode.Trim();
        if (code.Length is 0 or > 64)
        {
            throw new CatalogValidationException("Штрихкод: от 1 до 64 символов.");
        }

        var store = tenant.StoreId
            ?? throw new CatalogValidationException("В токене нет филиала: войдите с выбором филиала.");
        return catalog.FindByBarcodeAsync(code, store, ct);
    }

    /// <summary>
    /// До 50 товаров, чьё название содержит строку, со всеми их парами, по названию.
    /// Совпадений больше 50 — вернутся 50 любых из них: кассир уточняет запрос.
    /// («Первые 50 по алфавиту» потребовали бы сортировать все совпадения, и
    /// частый запрос снова рос бы вместе с числом товаров.)
    /// </summary>
    public Task<IReadOnlyList<SearchProductView>> SearchAsync(string query, CancellationToken ct)
    {
        // От 3 символов: поиск идёт по триграммам, из двух букв индекс не построить
        // (см. db/migrations/0003_product_search.sql).
        var q = query.Trim();
        if (q.Length is < 3 or > 100)
        {
            throw new CatalogValidationException("Поиск: от 3 до 100 символов.");
        }

        return catalog.SearchByNameAsync(q, SearchLimit, ct);
    }

    public Task<ProductView?> GetAsync(Guid productId, CancellationToken ct) =>
        catalog.GetAsync(productId, tenant.StoreId, ct);

    public Task<WriteResult> CreateAsync(NewProduct product, CancellationToken ct)
    {
        if (product.Id == Guid.Empty)
        {
            throw new CatalogValidationException("id товара обязателен: его создаёт клиент.");
        }

        RequireText(product.Name, "Название", 200);
        Optional(product.Brand, "Бренд", 100);
        Optional(product.Article, "Артикул", 100);
        Optional(product.Description, "Описание", 4000);
        ValidateVariants(product.Variants, allowEmpty: false);
        return catalog.CreateAsync(product, ct);
    }

    public Task<WriteResult> AddVariantsAsync(Guid productId, IReadOnlyList<NewVariant> variants, CancellationToken ct)
    {
        ValidateVariants(variants, allowEmpty: false);
        return catalog.AddVariantsAsync(productId, variants, ct);
    }

    private static void ValidateVariants(IReadOnlyList<NewVariant>? variants, bool allowEmpty)
    {
        if (variants is null || (!allowEmpty && variants.Count == 0))
        {
            throw new CatalogValidationException("Нужна хотя бы одна пара (размер + цвет).");
        }

        if (variants.Count > MaxVariantsPerRequest)
        {
            throw new CatalogValidationException($"Не больше {MaxVariantsPerRequest} пар за запрос.");
        }

        foreach (var v in variants)
        {
            if (v.Id == Guid.Empty)
            {
                throw new CatalogValidationException("id пары обязателен: его создаёт клиент.");
            }

            RequireText(v.Size, "Размер", 20);
            RequireText(v.Color, "Цвет", 50);
            Optional(v.Barcode, "Штрихкод", 64);
            if (v.CostPrice < 0 || v.SalePrice < 0 || v.CostPrice > 999_999_999_999.99m || v.SalePrice > 999_999_999_999.99m)
            {
                throw new CatalogValidationException("Цена: от 0 до 999 999 999 999.99.");
            }

            if (decimal.Round(v.CostPrice, 2) != v.CostPrice || decimal.Round(v.SalePrice, 2) != v.SalePrice)
            {
                throw new CatalogValidationException("Цена — не больше двух знаков после запятой.");
            }
        }

        if (variants.Select(v => v.Id).Distinct().Count() != variants.Count)
        {
            throw new CatalogValidationException("id пар в запросе повторяются.");
        }

        if (variants.Select(v => (Size: v.Size.Trim().ToLowerInvariant(), Color: v.Color.Trim().ToLowerInvariant())).Distinct().Count() != variants.Count)
        {
            throw new CatalogValidationException("Пара размер + цвет в запросе повторяется.");
        }

        var barcodes = variants.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)).Select(v => v.Barcode!.Trim()).ToList();
        if (barcodes.Distinct().Count() != barcodes.Count)
        {
            throw new CatalogValidationException("Штрихкоды в запросе повторяются.");
        }
    }

    private static void RequireText(string? value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
        {
            throw new CatalogValidationException($"{field}: обязательно, до {max} символов.");
        }
    }

    private static void Optional(string? value, string field, int max)
    {
        if (value is not null && value.Trim().Length > max)
        {
            throw new CatalogValidationException($"{field}: до {max} символов.");
        }
    }
}
