namespace KassirERP.Application.Stock;

// Списание и инвентаризация (партия B). Филиал — из токена; id документов
// создаёт клиент (повтор того же запроса — не дубль). Движения склада делает
// база (миграция 0010): writeoff −qty сразу, inventory counted − book при проведении.

public static class WriteoffReasons
{
    public const string Spoilage = "spoilage";   // порча
    public const string Expired = "expired";     // просрочка
    public const string Defect = "defect";       // брак

    public static readonly IReadOnlyList<string> All = [Spoilage, Expired, Defect];
}

public sealed record NewWriteoffItem(Guid Id, Guid VariantId, decimal Qty);

public sealed record NewWriteoff(Guid Id, string Reason, string? Note, IReadOnlyList<NewWriteoffItem> Items);

public sealed record WriteoffItemView(Guid Id, Guid VariantId, decimal Qty, decimal CostPrice);

public sealed record WriteoffView(
    Guid Id, Guid StoreId, string Reason, string? Note, DateTimeOffset CreatedAt, IReadOnlyList<WriteoffItemView> Items);

public sealed record OpenInventory(Guid Id, string? Number);

/// <summary>Посчитанное количество пары. Повторный подсчёт пары перезаписывает прежний.</summary>
public sealed record InventoryCount(Guid VariantId, decimal CountedQty);

public sealed record CountInventory(IReadOnlyList<InventoryCount> Items);

/// <summary>BookQty — учётный остаток в момент подсчёта; Difference = Counted − Book (уйдёт движением при проведении).</summary>
public sealed record InventoryItemView(Guid Id, Guid VariantId, decimal CountedQty, decimal BookQty, decimal Difference, DateTimeOffset CountedAt);

public sealed record InventoryView(
    Guid Id, Guid StoreId, string? Number, string Status, DateTimeOffset OpenedAt, DateTimeOffset? PostedAt,
    IReadOnlyList<InventoryItemView> Items);

public enum StockDocStatus
{
    Done,
    /// <summary>Повтор того же запроса: документ уже такой.</summary>
    AlreadyDone,
    NotFound,
    /// <summary>Тот же id с другим содержимым; в филиале уже открыта другая инвентаризация.</summary>
    Conflict,
    /// <summary>Документ другого филиала.</summary>
    WrongStore,
    /// <summary>Инвентаризация уже проведена: подсчёт не принимается.</summary>
    WrongState,
    Invalid,
}

public sealed record WriteoffResult(StockDocStatus Status, string? Detail = null, WriteoffView? Writeoff = null);

public sealed record InventoryResult(StockDocStatus Status, string? Detail = null, InventoryView? Inventory = null);

/// <summary>Списания и инвентаризации через TenantDb.</summary>
public interface IStockDocumentStore
{
    Task<WriteoffResult> CreateWriteoffAsync(NewWriteoff writeoff, Guid storeId, Guid userId, CancellationToken ct);
    Task<WriteoffView?> GetWriteoffAsync(Guid id, CancellationToken ct);

    Task<InventoryResult> OpenInventoryAsync(OpenInventory inventory, Guid storeId, Guid userId, CancellationToken ct);
    Task<InventoryResult> CountAsync(Guid inventoryId, IReadOnlyList<InventoryCount> items, Guid storeId, Guid userId, CancellationToken ct);
    Task<InventoryResult> PostInventoryAsync(Guid inventoryId, Guid storeId, Guid userId, CancellationToken ct);
    Task<InventoryView?> GetInventoryAsync(Guid id, CancellationToken ct);
}
