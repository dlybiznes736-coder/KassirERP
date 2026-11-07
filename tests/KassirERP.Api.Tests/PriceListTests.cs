using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KassirERP.Api.Tests.Infrastructure;

namespace KassirERP.Api.Tests;

/// <summary>
/// Прайс-листы: цена филиала = цена его листа, иначе базовая; журнал цен.
/// Листы назначаются только филиалам 2 и 3 фирмы A и снимаются в конце теста:
/// филиал 1 (на нём работают остальные тесты) остаётся на базовых ценах.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PriceListTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Stores_with_different_lists_get_different_prices_and_store_without_list_gets_base()
    {
        var manager = await TokenAsync("manager");
        var list2 = await NewListAsync(manager, "Зона 2", (_data.A.VariantId, 5100.00m));
        var list3 = await NewListAsync(manager, "Зона 3", (_data.A.VariantId, 4800.00m));
        try
        {
            await AssignAsync(manager, _data.A.Store2Id, list2);
            await AssignAsync(manager, _data.A.Store3Id, list3);

            var store1 = await ScanAsync(_data.A.StoreId);
            var store2 = await ScanAsync(_data.A.Store2Id);
            var store3 = await ScanAsync(_data.A.Store3Id);

            Assert.Equal(4990.00m, store1.GetProperty("salePrice").GetDecimal());           // нет листа — базовая
            Assert.Equal(JsonValueKind.Null, store1.GetProperty("priceListId").ValueKind);
            Assert.Equal(5100.00m, store2.GetProperty("salePrice").GetDecimal());
            Assert.Equal(list2, store2.GetProperty("priceListId").GetGuid());
            Assert.Equal(4800.00m, store3.GetProperty("salePrice").GetDecimal());
            Assert.Equal(4990.00m, store3.GetProperty("basePrice").GetDecimal());

            // Пары, которой нет в листе, — базовая цена и в филиале с листом.
            var weighted = await ScanAsync(_data.A.Store2Id, WeightCode(_data.A.Plu, 1000));
            Assert.Equal(_data.A.PricePerKg, weighted.GetProperty("salePrice").GetDecimal());
        }
        finally
        {
            await AssignAsync(manager, _data.A.Store2Id, null);
            await AssignAsync(manager, _data.A.Store3Id, null);
        }
    }

    [Fact]
    public async Task Search_and_product_card_show_the_store_price()
    {
        var manager = await TokenAsync("manager");
        var list = await NewListAsync(manager, "Зона поиска", (_data.A.VariantId, 5555.00m));
        try
        {
            await AssignAsync(manager, _data.A.Store2Id, list);
            var owner = await TokenAsync("owner", _data.A.Store2Id);

            var search = await (await Send(HttpMethod.Get, $"/products/search?q={Uri.EscapeDataString(_data.Suffix)}", owner)).JsonAsync();
            var card = await (await Send(HttpMethod.Get, $"/products/{_data.A.ProductId}", owner)).JsonAsync();

            var found = search.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == _data.A.ProductId);
            Assert.Equal(5555.00m, found.GetProperty("variants")[0].GetProperty("salePrice").GetDecimal());
            Assert.Equal(5555.00m, card.GetProperty("variants")[0].GetProperty("salePrice").GetDecimal());
            Assert.Equal(4990.00m, card.GetProperty("variants")[0].GetProperty("basePrice").GetDecimal());
        }
        finally
        {
            await AssignAsync(manager, _data.A.Store2Id, null);
        }
    }

    [Fact]
    public async Task Price_changes_are_journaled_who_when_old_new()
    {
        var manager = await TokenAsync("manager");
        var managerId = _data.A.Users["manager"].Id;
        var list = await NewListAsync(manager, "Журнал", (_data.A.VariantId, 5100.00m));

        await SetPricesAsync(manager, list, (_data.A.VariantId, 5200.00m));
        await SetPricesAsync(manager, list, (_data.A.VariantId, 5200.00m));   // та же цена — записи нет
        var removed = await Send(HttpMethod.Delete, $"/price-lists/{list}/items/{_data.A.VariantId}", manager);
        var journal = await (await Send(HttpMethod.Get, $"/price-changes?priceListId={list}", manager)).JsonAsync();

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var entries = journal.EnumerateArray().Reverse().ToList();   // по времени
        Assert.Equal(3, entries.Count);
        Assert.Equal((null, 5100.00m), Prices(entries[0]));
        Assert.Equal((5100.00m, 5200.00m), Prices(entries[1]));
        Assert.Equal((5200.00m, null), Prices(entries[2]));
        Assert.All(entries, e => Assert.Equal(managerId, e.GetProperty("changedBy").GetGuid()));
        Assert.All(entries, e => Assert.True(e.GetProperty("changedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public async Task Only_managers_change_prices_and_lists_stay_inside_the_tenant()
    {
        var keeper = await TokenAsync("keeper");
        var managerA = await TokenAsync("manager");
        var managerB = (await _client.LoginAsync(_data.B, "manager")).AccessToken;
        var listB = await NewListAsync(managerB, "Лист фирмы B", (_data.B.VariantId, 1.00m));

        var keeperCreates = await Send(HttpMethod.Post, "/price-lists", keeper, new { id = Guid.NewGuid(), name = "Нельзя" });
        var foreignAssign = await Send(HttpMethod.Put, $"/stores/{_data.A.Store2Id}/price-list", managerA, new { priceListId = listB });
        var foreignItems = await Send(HttpMethod.Get, $"/price-lists/{listB}/items", managerA);
        var foreignVariant = await Send(HttpMethod.Put, $"/price-lists/{await NewListAsync(managerA, "Пустой")}/items", managerA,
            new { items = new[] { new { variantId = _data.B.VariantId, salePrice = 1.00m } } });

        Assert.Equal(HttpStatusCode.Forbidden, keeperCreates.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignAssign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignItems.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignVariant.StatusCode);
    }

    // -----------------------------------------------------------------------

    private static (decimal?, decimal?) Prices(JsonElement e) =>
        (e.GetProperty("oldPrice").ValueKind == JsonValueKind.Null ? null : e.GetProperty("oldPrice").GetDecimal(),
         e.GetProperty("newPrice").ValueKind == JsonValueKind.Null ? null : e.GetProperty("newPrice").GetDecimal());

    private async Task<string> TokenAsync(string user, Guid? storeId = null) =>
        (await _client.LoginAsync(_data.A, user, storeId)).AccessToken;

    private async Task<JsonElement> ScanAsync(Guid storeId, string? code = null)
    {
        var owner = await TokenAsync("owner", storeId);
        var response = await Send(HttpMethod.Get, $"/products/barcode/{code ?? _data.A.Barcode}", owner);
        response.EnsureSuccessStatusCode();
        return await response.JsonAsync();
    }

    private async Task<Guid> NewListAsync(string token, string name, params (Guid Variant, decimal Price)[] prices)
    {
        var id = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/price-lists", token, new { id, name = $"{name} {id:N}" })).EnsureSuccessStatusCode();
        if (prices.Length > 0)
        {
            await SetPricesAsync(token, id, prices);
        }

        return id;
    }

    private async Task SetPricesAsync(string token, Guid list, params (Guid Variant, decimal Price)[] prices) =>
        (await Send(HttpMethod.Put, $"/price-lists/{list}/items", token,
            new { items = prices.Select(p => new { variantId = p.Variant, salePrice = p.Price }).ToArray() })).EnsureSuccessStatusCode();

    private async Task AssignAsync(string token, Guid store, Guid? list) =>
        (await Send(HttpMethod.Put, $"/stores/{store}/price-list", token, new { priceListId = list })).EnsureSuccessStatusCode();

    private static string WeightCode(string plu, int grams)
    {
        var first12 = "22" + plu + grams.ToString("D5");
        return first12 + KassirERP.Application.Catalog.WeightBarcode.CheckDigit(first12);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }
}
