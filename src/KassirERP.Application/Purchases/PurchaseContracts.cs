namespace KassirERP.Application.Purchases;

// id накладной и строк создаёт клиент (принцип 1 ТЗ): повторная отправка той же
// накладной не создаёт дубль. Филиал и тенант — из токена, в запросе их нет.
public sealed record NewPurchaseItem(Guid Id, Guid VariantId, decimal Qty, decimal CostPrice, DateOnly? ExpiresOn = null);

public sealed record NewPurchase(Guid Id, Guid? SupplierId, string? Number, DateOnly? DocDate, IReadOnlyList<NewPurchaseItem> Items);

public sealed record PurchaseItemView(Guid Id, Guid VariantId, decimal Qty, decimal CostPrice, DateOnly? ExpiresOn);

public sealed record PurchaseView(
    Guid Id, Guid StoreId, Guid? SupplierId, string? Number, DateOnly DocDate, string Status, decimal Total,
    DateTimeOffset? AcceptedAt, IReadOnlyList<PurchaseItemView> Items);

public enum PurchaseStatus
{
    /// <summary>Создана (или проведена) этим запросом.</summary>
    Done,
    /// <summary>Повтор того же запроса: документ уже такой, ничего не изменилось.</summary>
    AlreadyDone,
    NotFound,
    /// <summary>Тот же id, другое содержимое — или id занят.</summary>
    Conflict,
    /// <summary>Накладная проведена: менять и удалять нельзя.</summary>
    Locked,
    /// <summary>Пара или поставщик не найдены в своей фирме.</summary>
    UnknownReference,
    /// <summary>У пары включён учёт сроков, а срок годности не указан.</summary>
    ExpiryRequired,
    /// <summary>Нечего проводить: в накладной нет строк.</summary>
    Empty,
}

public sealed record PurchaseResult(PurchaseStatus Status, string? Detail = null, PurchaseView? Purchase = null);

/// <summary>Накладные прихода. Все методы — в тенанте из токена (TenantDb).</summary>
public interface IPurchaseStore
{
    Task<PurchaseResult> CreateAsync(NewPurchase purchase, Guid storeId, Guid userId, CancellationToken ct);
    Task<PurchaseResult> ReplaceItemsAsync(Guid purchaseId, IReadOnlyList<NewPurchaseItem> items, CancellationToken ct);
    Task<PurchaseResult> DeleteAsync(Guid purchaseId, CancellationToken ct);

    /// <summary>Проведение: draft → accepted, триггер создаёт движения склада (со сроком годности).</summary>
    Task<PurchaseResult> AcceptAsync(Guid purchaseId, Guid userId, CancellationToken ct);

    Task<PurchaseView?> GetAsync(Guid purchaseId, CancellationToken ct);
}
