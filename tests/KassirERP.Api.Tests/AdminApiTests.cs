using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KassirERP.Api.Tests.Infrastructure;
using KassirERP.Application.Catalog;

namespace KassirERP.Api.Tests;

/// <summary>
/// API вместо SQL (партия C): прайс-листы и цены, доступ кассиров к филиалам,
/// весовые префиксы. Кассир — 403 на всё это.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminApiTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Cashier_gets_403_on_prices_store_access_and_weight_prefixes()
    {
        var cashier = (await _client.LoginAsync(_data.A, "cashier")).AccessToken;
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var list = Guid.NewGuid();
        var user = _data.A.Users["nostore"].Id;
        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/price-lists", null),
            (HttpMethod.Post, "/price-lists", new { id = list, name = "Кассир" }),
            (HttpMethod.Get, $"/price-lists/{list}/items", null),
            (HttpMethod.Put, $"/price-lists/{list}/items", new { items = new[] { new { variantId = _data.A.VariantId, salePrice = 1m } } }),
            (HttpMethod.Patch, $"/price-lists/{list}", new { name = "Кассир 2" }),
            (HttpMethod.Delete, $"/price-lists/{list}", null),
            (HttpMethod.Delete, $"/price-lists/{list}/items/{_data.A.VariantId}", null),
            (HttpMethod.Get, "/price-changes", null),
            (HttpMethod.Put, $"/stores/{_data.A.StoreId}/price-list", new { priceListId = (Guid?)null }),
            (HttpMethod.Put, "/base-prices", new { items = new[] { new { variantId = _data.A.VariantId, salePrice = 1m } } }),
            (HttpMethod.Get, "/users", null),
            (HttpMethod.Get, $"/users/{user}", null),
            (HttpMethod.Put, $"/users/{user}/stores", new { storeIds = new[] { _data.A.StoreId } }),
            (HttpMethod.Get, "/weight-prefixes", null),
            (HttpMethod.Put, "/weight-prefixes", new { prefixes = new[] { "22" } }),
        };

        var cashierCodes = new List<HttpStatusCode>();
        foreach (var (method, url, body) in calls)
        {
            cashierCodes.Add((await Send(method, url, cashier, body)).StatusCode);
        }

        // Кладовщик видит прайс-листы, но не меняет цены и не трогает настройки фирмы.
        var keeperRead = await Send(HttpMethod.Get, "/price-lists", keeper);
        var keeperBase = await Send(HttpMethod.Put, "/base-prices", keeper, calls[9].Body);
        var keeperUsers = await Send(HttpMethod.Get, "/users", keeper);
        var keeperPrefixes = await Send(HttpMethod.Put, "/weight-prefixes", keeper, calls[14].Body);

        Assert.All(cashierCodes.Select((code, i) => (code, calls[i].Url)), x => Assert.Equal(HttpStatusCode.Forbidden, x.code));
        Assert.Equal(HttpStatusCode.OK, keeperRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, keeperBase.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, keeperUsers.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, keeperPrefixes.StatusCode);
    }

    [Fact]
    public async Task Price_lists_and_base_prices_are_managed_through_the_api()
    {
        var manager = (await _client.LoginAsync(_data.A, "manager")).AccessToken;
        var (product, variant) = await NewGoodsAsync();
        var list = Guid.NewGuid();
        var other = Guid.NewGuid();
        await Send(HttpMethod.Post, "/price-lists", manager, new { id = list, name = $"Админ {_data.Suffix}" });
        await Send(HttpMethod.Post, "/price-lists", manager, new { id = other, name = $"Другой {_data.Suffix}" });

        var renamed = await Send(HttpMethod.Patch, $"/price-lists/{list}", manager, new { name = $"Админ новый {_data.Suffix}" });
        var sameName = await Send(HttpMethod.Patch, $"/price-lists/{list}", manager, new { name = $"Другой {_data.Suffix}" });
        await Send(HttpMethod.Put, $"/stores/{_data.A.Store2Id}/price-list", manager, new { priceListId = list });
        var deleteAssigned = await Send(HttpMethod.Delete, $"/price-lists/{list}", manager);
        await Send(HttpMethod.Put, $"/stores/{_data.A.Store2Id}/price-list", manager, new { priceListId = (Guid?)null });
        var deleted = await Send(HttpMethod.Delete, $"/price-lists/{list}", manager);
        var lists = await (await Send(HttpMethod.Get, "/price-lists", manager)).JsonAsync();

        var basePrice = await Send(HttpMethod.Put, "/base-prices", manager, new { items = new[] { new { variantId = variant, salePrice = 250m } } });
        var foreign = await Send(HttpMethod.Put, "/base-prices", manager, new { items = new[] { new { variantId = _data.B.VariantId, salePrice = 1m } } });
        var card = await (await Send(HttpMethod.Get, $"/products/{product}", manager)).JsonAsync();
        var journal = await (await Send(HttpMethod.Get, $"/price-changes?variantId={variant}", manager)).JsonAsync();

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteAssigned.StatusCode);       // лист у филиала — не удаляется
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.DoesNotContain(lists.EnumerateArray(), l => l.GetProperty("id").GetGuid() == list);
        Assert.Equal(HttpStatusCode.OK, basePrice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(250m, card.GetProperty("variants")[0].GetProperty("basePrice").GetDecimal());
        var entry = Assert.Single(journal.EnumerateArray());                    // базовая цена журналируется
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("priceListId").ValueKind);
        Assert.Equal((200m, 250m), (entry.GetProperty("oldPrice").GetDecimal(), entry.GetProperty("newPrice").GetDecimal()));
        Assert.Equal(_data.A.Users["manager"].Id, entry.GetProperty("changedBy").GetGuid());
    }

    [Fact]
    public async Task Cashier_store_access_is_managed_through_the_api()
    {
        var manager = (await _client.LoginAsync(_data.A, "manager")).AccessToken;
        var user = _data.A.Users["nostore"];
        var kassa1 = _data.A.Devices["kassa1"];
        var kassa2 = _data.A.Devices["kassa2"];
        try
        {
            var before = await _client.PinAsync(kassa1, user.Login, user.Pin!);
            var granted = await (await Send(HttpMethod.Put, $"/users/{user.Id}/stores", manager, new { storeIds = new[] { _data.A.Store2Id } })).JsonAsync();
            var atStore1 = await _client.PinAsync(kassa1, user.Login, user.Pin!);
            var atStore2 = await _client.PinAsync(kassa2, user.Login, user.Pin!);
            var foreignStore = await Send(HttpMethod.Put, $"/users/{user.Id}/stores", manager, new { storeIds = new[] { _data.B.StoreId } });
            var foreignUser = await Send(HttpMethod.Put, $"/users/{_data.B.Users["cashier"].Id}/stores", manager, new { storeIds = new[] { _data.A.StoreId } });
            var list = await (await Send(HttpMethod.Get, "/users", manager)).JsonAsync();

            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);              // пустой список — по PIN никуда
            Assert.Equal([_data.A.Store2Id], granted.GetProperty("stores").EnumerateArray().Select(s => s.GetGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, atStore1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, atStore2.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, foreignStore.StatusCode);       // филиал чужой фирмы
            Assert.Equal(HttpStatusCode.NotFound, foreignUser.StatusCode);          // сотрудник чужой фирмы не виден
            Assert.DoesNotContain(list.EnumerateArray(), u => u.GetProperty("id").GetGuid() == _data.B.Users["cashier"].Id);
            Assert.All(list.EnumerateArray(), u => Assert.False(u.TryGetProperty("passwordHash", out _)));
        }
        finally
        {
            await Send(HttpMethod.Put, $"/users/{user.Id}/stores", manager, new { storeIds = Array.Empty<Guid>() });
        }
    }

    [Fact]
    public async Task Weight_prefixes_are_managed_through_the_api()
    {
        var manager = (await _client.LoginAsync(_data.A, "manager")).AccessToken;
        try
        {
            var initial = await (await Send(HttpMethod.Get, "/weight-prefixes", manager)).JsonAsync();
            var set = await (await Send(HttpMethod.Put, "/weight-prefixes", manager, new { prefixes = new[] { "22" } })).JsonAsync();
            var with22 = await Send(HttpMethod.Get, $"/products/barcode/{Weighted("22")}", manager);
            var with23 = await Send(HttpMethod.Get, $"/products/barcode/{Weighted("23")}", manager);
            var invalid = await Send(HttpMethod.Put, "/weight-prefixes", manager, new { prefixes = new[] { "30" } });
            var reset = await (await Send(HttpMethod.Put, "/weight-prefixes", manager, new { prefixes = Array.Empty<string>() })).JsonAsync();
            var again23 = await Send(HttpMethod.Get, $"/products/barcode/{Weighted("23")}", manager);

            Assert.True(initial.GetProperty("allPrefixes").GetBoolean());
            Assert.Equal(["22"], set.GetProperty("prefixes").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(HttpStatusCode.OK, with22.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, with23.StatusCode);              // 23 отдан не весам
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(reset.GetProperty("allPrefixes").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, again23.StatusCode);                   // снова все 20–29
        }
        finally
        {
            await Send(HttpMethod.Put, "/weight-prefixes", manager, new { prefixes = Array.Empty<string>() });
        }
    }

    // -----------------------------------------------------------------------

    /// <summary>Весовой штрихкод товара тенанта A: префикс, PLU, 1.000 кг, контрольная цифра.</summary>
    private string Weighted(string prefix)
    {
        var first12 = prefix + _data.A.Plu + "01000";
        return first12 + WeightBarcode.CheckDigit(first12);
    }

    private async Task<(Guid Product, Guid Variant)> NewGoodsAsync()
    {
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/products", keeper, new
        {
            id = product, name = $"Админ {_data.Suffix} {product:N}",
            variants = new[] { new { id = variant, costPrice = 100m, salePrice = 200m } },
        })).EnsureSuccessStatusCode();
        return (product, variant);
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
