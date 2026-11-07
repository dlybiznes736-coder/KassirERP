namespace KassirERP.Application.Catalog;

/// <summary>Пара в ответе. Stock — остаток в филиале из токена (null, если филиала в токене нет).</summary>
public sealed record VariantView(Guid Id, string Size, string Color, string? Barcode, decimal SalePrice, decimal? Stock);

public sealed record ProductView(Guid Id, Guid? CategoryId, string Name, string? Brand, string? Article, IReadOnlyList<VariantView> Variants);

/// <summary>Результат скана штрихкода на кассе.</summary>
public sealed record ScanView(
    Guid VariantId, Guid ProductId, string Name, string? Brand, string? Article,
    string Size, string Color, string Barcode, decimal SalePrice, decimal Stock);

/// <summary>Строка поиска: товар и все его пары (без остатков — как в bench.sql).</summary>
public sealed record SearchProductView(Guid Id, string Name, string? Brand, string? Article, IReadOnlyList<SearchVariantView> Variants);

public sealed record SearchVariantView(Guid Id, string Size, string Color, string? Barcode, decimal SalePrice);

// id приходят с клиента (принцип 1 ТЗ). tenant_id в командах нет и быть не может.
public sealed record NewVariant(Guid Id, string Size, string Color, string? Barcode, decimal CostPrice, decimal SalePrice);

public sealed record NewProduct(Guid Id, Guid? CategoryId, string Name, string? Brand, string? Article, string? Description, IReadOnlyList<NewVariant> Variants);

public enum WriteStatus
{
    Created,
    NotFound,
    /// <summary>Такой id, штрихкод или пара размер+цвет уже есть.</summary>
    Conflict,
    /// <summary>Категория не найдена в своей фирме.</summary>
    UnknownCategory,
}

public sealed record WriteResult(WriteStatus Status, string? Detail = null);

/// <summary>Доступ к товарам. Все методы работают в тенанте из ITenantContext — другого входа нет.</summary>
public interface IProductCatalog
{
    Task<ScanView?> FindByBarcodeAsync(string barcode, Guid storeId, CancellationToken ct);
    /// <summary>До limit товаров, чьё название содержит query (буквально, без шаблонов), со всеми парами.</summary>
    Task<IReadOnlyList<SearchProductView>> SearchByNameAsync(string query, int limit, CancellationToken ct);
    Task<ProductView?> GetAsync(Guid productId, Guid? storeId, CancellationToken ct);
    Task<WriteResult> CreateAsync(NewProduct product, CancellationToken ct);
    Task<WriteResult> AddVariantsAsync(Guid productId, IReadOnlyList<NewVariant> variants, CancellationToken ct);
}
