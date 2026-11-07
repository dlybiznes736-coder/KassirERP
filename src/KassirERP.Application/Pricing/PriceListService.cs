using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Pricing;

public sealed class PricingValidationException(string message) : AppValidationException(message);

public sealed class PriceListService(IPriceListStore store)
{
    public const int MaxItemsPerRequest = 5000;

    public Task<PricingResult> CreateAsync(NewPriceList list, CancellationToken ct)
    {
        if (list.Id == Guid.Empty || string.IsNullOrWhiteSpace(list.Name) || list.Name.Trim().Length > 100)
        {
            throw new PricingValidationException("Нужны id листа (создаёт клиент) и название до 100 символов.");
        }

        return store.CreateAsync(list with { Name = list.Name.Trim() }, ct);
    }

    public Task<IReadOnlyList<PriceListView>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public Task<IReadOnlyList<PriceListItemView>?> GetItemsAsync(Guid listId, CancellationToken ct) => store.GetItemsAsync(listId, ct);

    public Task<PricingResult> SetPricesAsync(Guid listId, SetPrices request, CancellationToken ct)
    {
        var items = request.Items;
        if (items is null || items.Count == 0 || items.Count > MaxItemsPerRequest)
        {
            throw new PricingValidationException($"От 1 до {MaxItemsPerRequest} цен за запрос.");
        }

        foreach (var item in items)
        {
            if (item.VariantId == Guid.Empty || item.SalePrice < 0 || item.SalePrice > 999_999_999_999.99m
                || decimal.Round(item.SalePrice, 2) != item.SalePrice)
            {
                throw new PricingValidationException("Цена: нужна пара и сумма от 0, не больше двух знаков после запятой.");
            }
        }

        if (items.Select(i => i.VariantId).Distinct().Count() != items.Count)
        {
            throw new PricingValidationException("Пара в запросе повторяется.");
        }

        return store.SetPricesAsync(listId, items, ct);
    }

    public Task<PricingResult> RemovePriceAsync(Guid listId, Guid variantId, CancellationToken ct) => store.RemovePriceAsync(listId, variantId, ct);

    public Task<PricingResult> AssignToStoreAsync(Guid storeId, AssignPriceList request, CancellationToken ct) =>
        store.AssignToStoreAsync(storeId, request.PriceListId, ct);

    public Task<IReadOnlyList<PriceChangeView>> ChangesAsync(Guid? listId, Guid? variantId, CancellationToken ct) =>
        store.ChangesAsync(listId, variantId, ct);
}
