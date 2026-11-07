using System.Net;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;

namespace KassirERP.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class CatalogTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Scan_returns_product_with_stock_of_store_from_token()
    {
        var cashier = await _client.LoginAsync(_data.A, "cashier");

        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.A.Barcode}", cashier.AccessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(_data.A.ProductName, body.GetProperty("name").GetString());
        Assert.Equal("42", body.GetProperty("size").GetString());
        Assert.Equal(4990m, body.GetProperty("salePrice").GetDecimal());
        Assert.Equal(_data.A.Stock, body.GetProperty("stock").GetDecimal());
    }

    [Fact]
    public async Task Scan_without_store_in_token_is_bad_request()
    {
        var owner = await _client.LoginAsync(_data.A, "owner");

        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.A.Barcode}", owner.AccessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_product_with_variants_then_get_search_and_scan_it()
    {
        var keeper = await _client.LoginAsync(_data.A, "keeper");
        var productId = Guid.NewGuid();
        var barcode = $"C{_data.Suffix}1";
        var product = new
        {
            id = productId,
            name = $"Кеды Каталог {_data.Suffix}",
            brand = "Тест",
            article = "K-1",
            variants = new[]
            {
                new { id = Guid.NewGuid(), size = "40", color = "белый", barcode = (string?)barcode, costPrice = 1500.00m, salePrice = 2990.00m },
                new { id = Guid.NewGuid(), size = "41", color = "белый", barcode = (string?)null, costPrice = 1500.00m, salePrice = 2990.00m },
            },
        };

        var created = await _client.SendAsync(Json(HttpMethod.Post, "/products", keeper.AccessToken, product));
        var again = await _client.SendAsync(Json(HttpMethod.Post, "/products", keeper.AccessToken, product));
        var card = await _client.SendAsync(ApiClient.Get($"/products/{productId}", keeper.AccessToken));
        var search = await _client.SendAsync(ApiClient.Get($"/products/search?q={Uri.EscapeDataString($"Каталог {_data.Suffix}")}", keeper.AccessToken));
        var scan = await _client.SendAsync(ApiClient.Get($"/products/barcode/{barcode}", keeper.AccessToken));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // тот же id — второй раз не создаётся
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Equal(2, (await card.JsonAsync()).GetProperty("variants").GetArrayLength());
        var found = Assert.Single((await search.JsonAsync()).EnumerateArray());
        Assert.Equal(2, found.GetProperty("variants").GetArrayLength());
        Assert.Equal(0m, (await scan.JsonAsync()).GetProperty("stock").GetDecimal()); // прихода не было
    }

    [Fact]
    public async Task Add_variant_and_duplicate_barcode_conflict()
    {
        var keeper = await _client.LoginAsync(_data.A, "keeper");

        var add = await _client.SendAsync(Json(HttpMethod.Post, $"/products/{_data.A.ProductId}/variants", keeper.AccessToken, new
        {
            variants = new[] { new { id = Guid.NewGuid(), size = "43", color = "чёрный", barcode = $"V{_data.Suffix}", costPrice = 2500m, salePrice = 4990m } },
        }));
        var duplicate = await _client.SendAsync(Json(HttpMethod.Post, $"/products/{_data.A.ProductId}/variants", keeper.AccessToken, new
        {
            variants = new[] { new { id = Guid.NewGuid(), size = "44", color = "чёрный", barcode = _data.A.Barcode, costPrice = 2500m, salePrice = 4990m } },
        }));
        var foreign = await _client.SendAsync(Json(HttpMethod.Post, $"/products/{_data.B.ProductId}/variants", keeper.AccessToken, new
        {
            variants = new[] { new { id = Guid.NewGuid(), size = "45", color = "чёрный", barcode = (string?)null, costPrice = 1m, salePrice = 1m } },
        }));

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); // товар фирмы B для A не существует
    }

    [Fact]
    public async Task Cashier_cannot_create_products()
    {
        var cashier = await _client.LoginAsync(_data.A, "cashier");

        var response = await _client.SendAsync(Json(HttpMethod.Post, "/products", cashier.AccessToken, new
        {
            id = Guid.NewGuid(), name = "Нельзя", variants = new[] { new { id = Guid.NewGuid(), size = "1", color = "1", costPrice = 1m, salePrice = 1m } },
        }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("x")]       // короче 2 символов
    [InlineData("  ")]
    public async Task Too_short_search_is_bad_request(string q)
    {
        var cashier = await _client.LoginAsync(_data.A, "cashier");

        var response = await _client.SendAsync(ApiClient.Get($"/products/search?q={Uri.EscapeDataString(q)}", cashier.AccessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Like_wildcards_in_search_are_literal()
    {
        var cashier = await _client.LoginAsync(_data.A, "cashier");

        // «%%» без экранирования нашёл бы все товары.
        var response = await _client.SendAsync(ApiClient.Get("/products/search?q=%25%25", cashier.AccessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.JsonAsync()).GetArrayLength());
    }

    private static HttpRequestMessage Json(HttpMethod method, string url, string token, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
