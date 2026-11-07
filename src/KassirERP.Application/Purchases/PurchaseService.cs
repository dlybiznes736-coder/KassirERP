using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Purchases;

public sealed class PurchaseValidationException(string message) : AppValidationException(message);

/// <summary>Приём товара (шаг 4): накладная, проведение, движение склада, срок годности в строке.</summary>
public sealed class PurchaseService(IPurchaseStore store, ITenantContext tenant, TimeProvider time)
{
    public const int MaxItems = 500;

    public Task<PurchaseResult> CreateAsync(NewPurchase purchase, CancellationToken ct)
    {
        if (purchase.Id == Guid.Empty)
        {
            throw new PurchaseValidationException("id накладной обязателен: его создаёт клиент.");
        }

        if (purchase.Number is { Length: > 50 })
        {
            throw new PurchaseValidationException("Номер накладной: до 50 символов.");
        }

        var storeId = tenant.StoreId
            ?? throw new PurchaseValidationException("В токене нет филиала: приход оформляется в филиал, выбранный при входе.");
        var docDate = purchase.DocDate ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var items = Validate(purchase.Items, docDate);
        return store.CreateAsync(purchase with { DocDate = docDate, Number = Blank(purchase.Number), Items = items }, storeId, tenant.UserId, ct);
    }

    public async Task<PurchaseResult> ReplaceItemsAsync(Guid purchaseId, IReadOnlyList<NewPurchaseItem> items, CancellationToken ct)
    {
        var existing = await store.GetAsync(purchaseId, ct);
        if (existing is null)
        {
            return new(PurchaseStatus.NotFound);
        }

        return await store.ReplaceItemsAsync(purchaseId, Validate(items, existing.DocDate), ct);
    }

    public Task<PurchaseResult> DeleteAsync(Guid purchaseId, CancellationToken ct) => store.DeleteAsync(purchaseId, ct);

    public Task<PurchaseResult> AcceptAsync(Guid purchaseId, CancellationToken ct) => store.AcceptAsync(purchaseId, tenant.UserId, ct);

    public Task<PurchaseView?> GetAsync(Guid purchaseId, CancellationToken ct) => store.GetAsync(purchaseId, ct);

    private static List<NewPurchaseItem> Validate(IReadOnlyList<NewPurchaseItem>? items, DateOnly docDate)
    {
        if (items is null || items.Count == 0)
        {
            throw new PurchaseValidationException("Нужна хотя бы одна строка.");
        }

        if (items.Count > MaxItems)
        {
            throw new PurchaseValidationException($"Не больше {MaxItems} строк в накладной.");
        }

        foreach (var item in items)
        {
            if (item.Id == Guid.Empty || item.VariantId == Guid.Empty)
            {
                throw new PurchaseValidationException("У строки нужны id (создаёт клиент) и variantId.");
            }

            if (item.Qty <= 0 || item.Qty > 1_000_000_000m || decimal.Round(item.Qty, 3) != item.Qty)
            {
                throw new PurchaseValidationException("Количество: больше 0, не больше трёх знаков после запятой (граммы).");
            }

            if (item.CostPrice < 0 || item.CostPrice > 999_999_999_999.99m || decimal.Round(item.CostPrice, 2) != item.CostPrice)
            {
                throw new PurchaseValidationException("Цена закупки: от 0, не больше двух знаков после запятой.");
            }

            if (item.ExpiresOn is { } expires && expires < docDate)
            {
                throw new PurchaseValidationException($"Срок годности {expires:yyyy-MM-dd} раньше даты накладной: просроченный товар не принимается.");
            }
        }

        if (items.Select(i => i.Id).Distinct().Count() != items.Count)
        {
            throw new PurchaseValidationException("id строк в накладной повторяются.");
        }

        return [.. items];
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
