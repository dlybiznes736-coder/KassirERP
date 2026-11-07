namespace KassirERP.Application.Catalog;

public static class Units
{
    public const string Piece = "pcs";
    public const string Kilogram = "kg";
}

/// <summary>Пара в ответе. Stock — остаток в филиале из токена (null, если филиала в токене нет).</summary>
public sealed record VariantView(
    Guid Id, string? Size, string? Color, string? Barcode, string Unit, string? Plu, bool TracksExpiry, decimal SalePrice, decimal? Stock);

public sealed record ProductView(Guid Id, Guid? CategoryId, string Name, string? Brand, string? Article, IReadOnlyList<VariantView> Variants);

/// <summary>
/// Результат скана. Quantity — штуки (1) или вес из весового штрихкода в кг;
/// SalePrice — за штуку или за кг; LineTotal = round(SalePrice × Quantity, 2).
/// </summary>
public sealed record ScanView(
    Guid VariantId, Guid ProductId, string Name, string? Brand, string? Article,
    string? Size, string? Color, string? Barcode, string Unit, string? Plu,
    decimal SalePrice, decimal Quantity, decimal LineTotal, decimal Stock, string ScannedCode);

/// <summary>Строка поиска: товар и все его пары (без остатков — как в bench.sql).</summary>
public sealed record SearchProductView(Guid Id, string Name, string? Brand, string? Article, IReadOnlyList<SearchVariantView> Variants);

public sealed record SearchVariantView(Guid Id, string? Size, string? Color, string? Barcode, string Unit, decimal SalePrice);

// id приходят с клиента (принцип 1 ТЗ). tenant_id в командах нет и быть не может.
// Размер и цвет — для обуви; у продукта их нет. Весовой товар: Unit = "kg" и Plu.
public sealed record NewVariant(
    Guid Id, string? Size, string? Color, string? Barcode, decimal CostPrice, decimal SalePrice,
    string Unit = Units.Piece, string? Plu = null, bool TracksExpiry = false);

public sealed record NewProduct(Guid Id, Guid? CategoryId, string Name, string? Brand, string? Article, string? Description, IReadOnlyList<NewVariant> Variants);

/// <summary>Пара, найденная сканом (по штрихкоду или по PLU весового кода).</summary>
public sealed record ScanMatch(
    Guid VariantId, Guid ProductId, string Name, string? Brand, string? Article,
    string? Size, string? Color, string? Barcode, string Unit, string? Plu, decimal SalePrice, decimal Stock, bool ByPlu);

public enum WriteStatus
{
    Created,
    NotFound,
    /// <summary>Такой id, штрихкод, PLU или пара размер+цвет уже есть.</summary>
    Conflict,
    /// <summary>Категория не найдена в своей фирме.</summary>
    UnknownCategory,
}

public sealed record WriteResult(WriteStatus Status, string? Detail = null);

/// <summary>Доступ к товарам. Все методы работают в тенанте из ITenantContext — другого входа нет.</summary>
public interface IProductCatalog
{
    /// <summary>
    /// Точное совпадение штрихкода; если его нет и задан weight — весовая пара по PLU,
    /// при условии что префикс отдан весам (weight_barcode_prefixes; пусто — все 20–29).
    /// </summary>
    Task<ScanMatch?> FindByBarcodeAsync(string barcode, WeightBarcode? weight, Guid storeId, CancellationToken ct);

    /// <summary>До limit товаров, чьё название содержит query (буквально, без шаблонов), со всеми парами.</summary>
    Task<IReadOnlyList<SearchProductView>> SearchByNameAsync(string query, int limit, CancellationToken ct);

    Task<ProductView?> GetAsync(Guid productId, Guid? storeId, CancellationToken ct);
    Task<WriteResult> CreateAsync(NewProduct product, CancellationToken ct);
    Task<WriteResult> AddVariantsAsync(Guid productId, IReadOnlyList<NewVariant> variants, CancellationToken ct);
}
