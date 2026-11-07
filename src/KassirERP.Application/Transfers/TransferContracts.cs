namespace KassirERP.Application.Transfers;

// Перемещение (партия A): черновик → в пути → принят. Отправитель — филиал
// из токена; id документа и строк создаёт клиент (повтор — не дубль).
public sealed record NewTransferItem(Guid Id, Guid VariantId, decimal Qty);

public sealed record NewTransfer(Guid Id, Guid ToStoreId, string? Number, IReadOnlyList<NewTransferItem> Items);

/// <summary>Принятое количество по строке. Строки, которых нет в запросе, приняты полностью.</summary>
public sealed record ReceivedItem(Guid Id, decimal QtyReceived);

public sealed record ReceiveTransfer(IReadOnlyList<ReceivedItem>? Items);

/// <summary>Shortage = отправлено − принято (после приёма; 0 — без расхождения).</summary>
public sealed record TransferItemView(Guid Id, Guid VariantId, decimal QtySent, decimal? QtyReceived, decimal? Shortage);

public sealed record TransferView(
    Guid Id, Guid FromStoreId, Guid ToStoreId, string? Number, string Status, bool HasDiscrepancy,
    DateTimeOffset? SentAt, DateTimeOffset? ReceivedAt, IReadOnlyList<TransferItemView> Items);

public enum TransferStatus
{
    Done,
    /// <summary>Повтор того же запроса: документ уже такой.</summary>
    AlreadyDone,
    NotFound,
    /// <summary>Тот же id с другим содержимым или повтор приёма с другими количествами.</summary>
    Conflict,
    /// <summary>Действие из чужого филиала: отправляет отправитель, принимает получатель.</summary>
    WrongStore,
    /// <summary>Не тот этап: принять черновик нельзя.</summary>
    WrongState,
    Invalid,
}

public sealed record TransferResult(TransferStatus Status, string? Detail = null, TransferView? Transfer = null);

/// <summary>Перемещения через TenantDb. Движения склада делает база (миграция 0009).</summary>
public interface ITransferStore
{
    Task<TransferResult> CreateAsync(NewTransfer transfer, Guid fromStoreId, Guid userId, CancellationToken ct);
    Task<TransferResult> SendAsync(Guid transferId, Guid storeId, Guid userId, CancellationToken ct);
    Task<TransferResult> ReceiveAsync(Guid transferId, IReadOnlyList<ReceivedItem> received, Guid storeId, Guid userId, CancellationToken ct);
    Task<TransferView?> GetAsync(Guid transferId, CancellationToken ct);

    /// <summary>В пути в филиал (что ему предстоит принять).</summary>
    Task<IReadOnlyList<TransferView>> IncomingAsync(Guid storeId, CancellationToken ct);
}
