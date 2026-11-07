using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Stock;

public sealed class StockDocumentValidationException(string message) : AppValidationException(message);

/// <summary>Списание и инвентаризация. Филиал — из токена.</summary>
public sealed class StockDocumentService(IStockDocumentStore store, ITenantContext tenant)
{
    public const int MaxItems = 500;

    /// <summary>Подсчёт присылают пачками: за один запрос — до 5 000 пар.</summary>
    public const int MaxCountItems = 5000;

    public Task<WriteoffResult> CreateWriteoffAsync(NewWriteoff writeoff, CancellationToken ct)
    {
        var storeId = Store();
        if (writeoff.Id == Guid.Empty)
        {
            throw new StockDocumentValidationException("Нужен id списания (создаёт клиент).");
        }

        if (writeoff.Reason is null || !WriteoffReasons.All.Contains(writeoff.Reason))
        {
            throw new StockDocumentValidationException("Причина: spoilage (порча), expired (просрочка) или defect (брак).");
        }

        if (writeoff.Note is { Length: > 500 })
        {
            throw new StockDocumentValidationException("Комментарий: до 500 символов.");
        }

        if (writeoff.Items is null || writeoff.Items.Count == 0 || writeoff.Items.Count > MaxItems)
        {
            throw new StockDocumentValidationException($"От 1 до {MaxItems} строк.");
        }

        foreach (var item in writeoff.Items)
        {
            if (item.Id == Guid.Empty || item.VariantId == Guid.Empty)
            {
                throw new StockDocumentValidationException("У строки нужны id (создаёт клиент) и variantId.");
            }

            RequireQty(item.Qty, allowZero: false);
        }

        if (writeoff.Items.Select(i => i.Id).Distinct().Count() != writeoff.Items.Count)
        {
            throw new StockDocumentValidationException("id строк повторяются.");
        }

        var note = string.IsNullOrWhiteSpace(writeoff.Note) ? null : writeoff.Note.Trim();
        return store.CreateWriteoffAsync(writeoff with { Note = note }, storeId, tenant.UserId, ct);
    }

    public Task<WriteoffView?> GetWriteoffAsync(Guid id, CancellationToken ct) => store.GetWriteoffAsync(id, ct);

    public Task<InventoryResult> OpenInventoryAsync(OpenInventory inventory, CancellationToken ct)
    {
        var storeId = Store();
        if (inventory.Id == Guid.Empty)
        {
            throw new StockDocumentValidationException("Нужен id инвентаризации (создаёт клиент).");
        }

        if (inventory.Number is { Length: > 50 })
        {
            throw new StockDocumentValidationException("Номер: до 50 символов.");
        }

        var number = string.IsNullOrWhiteSpace(inventory.Number) ? null : inventory.Number.Trim();
        return store.OpenInventoryAsync(inventory with { Number = number }, storeId, tenant.UserId, ct);
    }

    public Task<InventoryResult> CountAsync(Guid inventoryId, CountInventory request, CancellationToken ct)
    {
        var storeId = Store();
        var items = request.Items;
        if (items is null || items.Count == 0 || items.Count > MaxCountItems)
        {
            throw new StockDocumentValidationException($"От 1 до {MaxCountItems} пар за запрос.");
        }

        foreach (var item in items)
        {
            if (item.VariantId == Guid.Empty)
            {
                throw new StockDocumentValidationException("У строки подсчёта нужен variantId.");
            }

            RequireQty(item.CountedQty, allowZero: true);
        }

        if (items.Select(i => i.VariantId).Distinct().Count() != items.Count)
        {
            throw new StockDocumentValidationException("Пара повторяется в одном запросе.");
        }

        return store.CountAsync(inventoryId, items, storeId, tenant.UserId, ct);
    }

    public Task<InventoryResult> PostInventoryAsync(Guid inventoryId, CancellationToken ct) =>
        store.PostInventoryAsync(inventoryId, Store(), tenant.UserId, ct);

    public Task<InventoryView?> GetInventoryAsync(Guid id, CancellationToken ct) => store.GetInventoryAsync(id, ct);

    private Guid Store() =>
        tenant.StoreId ?? throw new StockDocumentValidationException("В токене нет филиала: войдите с выбором филиала.");

    private static void RequireQty(decimal qty, bool allowZero)
    {
        if ((allowZero ? qty < 0 : qty <= 0) || qty > 1_000_000_000m || decimal.Round(qty, 3) != qty)
        {
            throw new StockDocumentValidationException("Количество: " + (allowZero ? "от 0" : "больше 0") + ", не больше трёх знаков после запятой.");
        }
    }
}
