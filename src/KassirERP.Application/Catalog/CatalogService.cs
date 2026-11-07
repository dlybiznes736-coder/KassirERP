using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Catalog;

public sealed class CatalogValidationException(string message) : AppValidationException(message);

/// <summary>Товары: скан, поиск, карточка, заведение моделей и пар.</summary>
public sealed class CatalogService(IProductCatalog catalog, ITenantContext tenant)
{
    public const int SearchLimit = 50;
    public const int MaxVariantsPerRequest = 200;

    /// <summary>
    /// Скан на кассе. Остаток — по филиалу из токена; без филиала скан невозможен.
    /// Сначала точный штрихкод, затем — весовой код 20–29 (PLU + вес).
    /// </summary>
    public async Task<ScanView?> ScanAsync(string barcode, CancellationToken ct)
    {
        var code = barcode.Trim();
        if (code.Length is 0 or > 64)
        {
            throw new CatalogValidationException("Штрихкод: от 1 до 64 символов.");
        }

        var store = tenant.StoreId
            ?? throw new CatalogValidationException("В токене нет филиала: войдите с выбором филиала.");

        var match = await catalog.FindByBarcodeAsync(code, WeightBarcode.TryParse(code), store, ct);
        if (match is null)
        {
            return null;
        }

        var quantity = match.ByPlu ? WeightBarcode.TryParse(code)!.Kilograms : 1m;
        var lineTotal = decimal.Round(match.SalePrice * quantity, 2, MidpointRounding.AwayFromZero);
        return new ScanView(
            match.VariantId, match.ProductId, match.Name, match.Brand, match.Article,
            match.Size, match.Color, match.Barcode, match.Unit, match.Plu,
            match.SalePrice, quantity, lineTotal, match.Stock, code);
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
        return catalog.CreateAsync(product with { Variants = NormalizeVariants(product.Variants) }, ct);
    }

    public Task<WriteResult> AddVariantsAsync(Guid productId, IReadOnlyList<NewVariant> variants, CancellationToken ct) =>
        catalog.AddVariantsAsync(productId, NormalizeVariants(variants), ct);

    /// <summary>Проверка пар; пустые размер, цвет, штрихкод и PLU — null.</summary>
    private static List<NewVariant> NormalizeVariants(IReadOnlyList<NewVariant>? variants)
    {
        if (variants is null || variants.Count == 0)
        {
            throw new CatalogValidationException("Нужна хотя бы одна пара.");
        }

        if (variants.Count > MaxVariantsPerRequest)
        {
            throw new CatalogValidationException($"Не больше {MaxVariantsPerRequest} пар за запрос.");
        }

        var result = new List<NewVariant>(variants.Count);
        foreach (var v in variants)
        {
            if (v.Id == Guid.Empty)
            {
                throw new CatalogValidationException("id пары обязателен: его создаёт клиент.");
            }

            Optional(v.Size, "Размер", 20);
            Optional(v.Color, "Цвет", 50);
            Optional(v.Barcode, "Штрихкод", 64);
            if (v.Unit is not (Units.Piece or Units.Kilogram))
            {
                throw new CatalogValidationException("Единица: pcs (штука) или kg (килограмм).");
            }

            var plu = Blank(v.Plu);
            if (plu is not null && (v.Unit != Units.Kilogram || plu.Length != 5 || !plu.All(char.IsAsciiDigit)))
            {
                throw new CatalogValidationException("PLU — 5 цифр и только у весового товара (unit = kg).");
            }

            if (v.CostPrice < 0 || v.SalePrice < 0 || v.CostPrice > 999_999_999_999.99m || v.SalePrice > 999_999_999_999.99m)
            {
                throw new CatalogValidationException("Цена: от 0 до 999 999 999 999.99.");
            }

            if (decimal.Round(v.CostPrice, 2) != v.CostPrice || decimal.Round(v.SalePrice, 2) != v.SalePrice)
            {
                throw new CatalogValidationException("Цена — не больше двух знаков после запятой.");
            }

            result.Add(v with { Size = Blank(v.Size), Color = Blank(v.Color), Barcode = Blank(v.Barcode), Plu = plu });
        }

        if (result.Select(v => v.Id).Distinct().Count() != result.Count)
        {
            throw new CatalogValidationException("id пар в запросе повторяются.");
        }

        if (result.Select(v => (Size: v.Size?.ToLowerInvariant(), Color: v.Color?.ToLowerInvariant())).Distinct().Count() != result.Count)
        {
            throw new CatalogValidationException("Пара размер + цвет в запросе повторяется (у товара без размера и цвета пара одна).");
        }

        foreach (var (name, values) in new[] { ("Штрихкоды", result.Select(v => v.Barcode)), ("PLU", result.Select(v => v.Plu)) })
        {
            var present = values.OfType<string>().ToList();
            if (present.Distinct().Count() != present.Count)
            {
                throw new CatalogValidationException($"{name} в запросе повторяются.");
            }
        }

        return result;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
