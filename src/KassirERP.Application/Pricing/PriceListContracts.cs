namespace KassirERP.Application.Pricing;

// Прайс-листы (партия A): лист назначается филиалам; цена пары в филиале —
// цена его листа, если она есть, иначе базовая. id листа создаёт клиент.
public sealed record NewPriceList(Guid Id, string Name);

public sealed record RenamePriceList(string Name);

/// <summary>Базовая цена пары (действует, если в листе филиала цены нет).</summary>
public sealed record BasePriceItem(Guid VariantId, decimal SalePrice);

public sealed record SetBasePrices(IReadOnlyList<BasePriceItem> Items);

public sealed record PriceItem(Guid VariantId, decimal SalePrice);

public sealed record SetPrices(IReadOnlyList<PriceItem> Items);

/// <summary>PriceListId = null — у филиала нет листа, действует базовая цена.</summary>
public sealed record AssignPriceList(Guid? PriceListId);

public sealed record PriceListView(Guid Id, string Name, int Items, IReadOnlyList<Guid> Stores);

public sealed record PriceListItemView(Guid VariantId, decimal SalePrice, DateTimeOffset UpdatedAt);

/// <summary>Запись журнала цен. PriceListId = null — базовая цена пары; OldPrice/NewPrice = null — цены не было / её убрали.</summary>
public sealed record PriceChangeView(Guid? PriceListId, Guid VariantId, decimal? OldPrice, decimal? NewPrice, Guid? ChangedBy, DateTimeOffset ChangedAt);

public enum PricingStatus
{
    Done,
    AlreadyDone,
    NotFound,
    Conflict,
    Invalid,
}

public sealed record PricingResult(PricingStatus Status, string? Detail = null, object? Body = null);

/// <summary>Прайс-листы. Всё — через TenantDb, в тенанте из токена; журнал пишет база.</summary>
public interface IPriceListStore
{
    Task<PricingResult> CreateAsync(NewPriceList list, CancellationToken ct);
    Task<IReadOnlyList<PriceListView>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<PriceListItemView>?> GetItemsAsync(Guid listId, CancellationToken ct);
    Task<PricingResult> SetPricesAsync(Guid listId, IReadOnlyList<PriceItem> items, CancellationToken ct);
    Task<PricingResult> RemovePriceAsync(Guid listId, Guid variantId, CancellationToken ct);
    Task<PricingResult> AssignToStoreAsync(Guid storeId, Guid? listId, CancellationToken ct);
    Task<IReadOnlyList<PriceChangeView>> ChangesAsync(Guid? listId, Guid? variantId, CancellationToken ct);
    Task<PricingResult> RenameAsync(Guid listId, string name, CancellationToken ct);

    /// <summary>Удалить лист можно, только если он не назначен ни одному филиалу.</summary>
    Task<PricingResult> DeleteAsync(Guid listId, CancellationToken ct);

    /// <summary>Базовые цены пар; журнал (price_list_id = null) пишет база.</summary>
    Task<PricingResult> SetBasePricesAsync(IReadOnlyList<BasePriceItem> items, CancellationToken ct);
}
