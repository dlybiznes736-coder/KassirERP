using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Transfers;

public sealed class TransferValidationException(string message) : AppValidationException(message);

/// <summary>Перемещение между филиалами. Филиал — из токена.</summary>
public sealed class TransferService(ITransferStore store, ITenantContext tenant)
{
    public const int MaxItems = 500;

    public Task<TransferResult> CreateAsync(NewTransfer transfer, CancellationToken ct)
    {
        var from = Store();
        if (transfer.Id == Guid.Empty || transfer.ToStoreId == Guid.Empty)
        {
            throw new TransferValidationException("Нужны id перемещения (создаёт клиент) и филиал-получатель.");
        }

        if (transfer.ToStoreId == from)
        {
            throw new TransferValidationException("Филиал-получатель совпадает с отправителем.");
        }

        if (transfer.Number is { Length: > 50 })
        {
            throw new TransferValidationException("Номер: до 50 символов.");
        }

        if (transfer.Items is null || transfer.Items.Count == 0 || transfer.Items.Count > MaxItems)
        {
            throw new TransferValidationException($"От 1 до {MaxItems} строк.");
        }

        foreach (var item in transfer.Items)
        {
            if (item.Id == Guid.Empty || item.VariantId == Guid.Empty)
            {
                throw new TransferValidationException("У строки нужны id (создаёт клиент) и variantId.");
            }

            RequireQty(item.Qty, allowZero: false);
        }

        if (transfer.Items.Select(i => i.Id).Distinct().Count() != transfer.Items.Count)
        {
            throw new TransferValidationException("id строк повторяются.");
        }

        var number = string.IsNullOrWhiteSpace(transfer.Number) ? null : transfer.Number.Trim();
        return store.CreateAsync(transfer with { Number = number }, from, tenant.UserId, ct);
    }

    public Task<TransferResult> SendAsync(Guid transferId, CancellationToken ct) =>
        store.SendAsync(transferId, Store(), tenant.UserId, ct);

    public Task<TransferResult> ReceiveAsync(Guid transferId, ReceiveTransfer request, CancellationToken ct)
    {
        var items = request.Items ?? [];
        foreach (var item in items)
        {
            if (item.Id == Guid.Empty)
            {
                throw new TransferValidationException("У строки приёма нужен id строки перемещения.");
            }

            RequireQty(item.QtyReceived, allowZero: true);
        }

        if (items.Select(i => i.Id).Distinct().Count() != items.Count)
        {
            throw new TransferValidationException("Строка приёма повторяется.");
        }

        return store.ReceiveAsync(transferId, items, Store(), tenant.UserId, ct);
    }

    public Task<TransferView?> GetAsync(Guid transferId, CancellationToken ct) => store.GetAsync(transferId, ct);

    public Task<IReadOnlyList<TransferView>> IncomingAsync(CancellationToken ct) => store.IncomingAsync(Store(), ct);

    private Guid Store() =>
        tenant.StoreId ?? throw new TransferValidationException("В токене нет филиала: войдите с выбором филиала.");

    private static void RequireQty(decimal qty, bool allowZero)
    {
        if ((allowZero ? qty < 0 : qty <= 0) || qty > 1_000_000_000m || decimal.Round(qty, 3) != qty)
        {
            throw new TransferValidationException("Количество: " + (allowZero ? "от 0" : "больше 0") + ", не больше трёх знаков после запятой.");
        }
    }
}
