using System.Net;
using KassirERP.Api.Tests.Infrastructure;
using KassirERP.Infrastructure.Auth;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Пользователь фирмы A пытается получить данные фирмы B, подставляя её
/// tenant_id в токен, в тело запроса и в URL. Каждая попытка — отказ, не данные.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Control_own_barcode_returns_product_with_stock_of_token_store()
    {
        var a = await _client.LoginAsync(_data.A, "cashier");

        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.A.Barcode}", a.AccessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(_data.A.VariantId, body.GetProperty("variantId").GetGuid());
        Assert.Equal(_data.A.Stock, body.GetProperty("stock").GetDecimal());
    }

    [Fact]
    public async Task Control_other_tenant_barcode_with_honest_token_is_not_found()
    {
        var a = await _client.LoginAsync(_data.A, "cashier");

        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.B.Barcode}", a.AccessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- 1. tenant_id тенанта B в токене ----------------------------------

    [Fact]
    public async Task Tenant_b_in_token_payload_is_rejected()
    {
        var a = await _client.LoginAsync(_data.A, "cashier");
        var forged = ApiClient.TamperClaim(a.AccessToken, JwtClaims.TenantId, _data.B.Id.ToString());
        forged = ApiClient.TamperClaim(forged, JwtClaims.StoreId, _data.B.StoreId.ToString());

        var scan = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.B.Barcode}", forged));
        var me = await _client.SendAsync(ApiClient.Get("/auth/me", forged));

        Assert.Equal(HttpStatusCode.Unauthorized, scan.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // --- 2. tenant_id тенанта B в теле запроса -----------------------------

    [Theory]
    [InlineData("tenantId")]
    [InlineData("tenant_id")]
    [InlineData("TenantId")]
    public async Task Tenant_b_in_request_body_is_rejected(string field)
    {
        var keeper = await _client.LoginAsync(_data.A, "keeper");
        var productId = Guid.NewGuid();
        var json = $$"""
            {
              "{{field}}": "{{_data.B.Id}}",
              "id": "{{productId}}",
              "name": "Подмена тенанта {{_data.Suffix}}",
              "variants": [ { "id": "{{Guid.NewGuid()}}", "size": "40", "color": "белый", "costPrice": 1, "salePrice": 2 } ]
            }
            """;

        var response = await _client.SendAsync(ApiClient.PostJson("/products", keeper.AccessToken, json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ProductExistsInAnyTenantAsync(productId), "товар не должен был создаться ни в одной фирме");
    }

    [Fact]
    public async Task Tenant_b_in_login_body_is_rejected()
    {
        var json = $$"""
            { "tenantCode": "{{_data.A.Code}}", "login": "keeper", "password": "{{_data.A.Users["keeper"].Password}}", "tenantId": "{{_data.B.Id}}" }
            """;

        var response = await _client.SendAsync(ApiClient.PostJson("/auth/login", null, json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- 3. tenant_id тенанта B в URL и заголовке --------------------------

    [Theory]
    [InlineData("tenantId")]
    [InlineData("tenant_id")]
    [InlineData("tenant")]
    public async Task Tenant_b_in_query_string_is_rejected(string parameter)
    {
        var a = await _client.LoginAsync(_data.A, "cashier");

        var scan = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.B.Barcode}?{parameter}={_data.B.Id}", a.AccessToken));
        var search = await _client.SendAsync(ApiClient.Get($"/products/search?q={Uri.EscapeDataString(_data.Suffix)}&{parameter}={_data.B.Id}", a.AccessToken));

        Assert.Equal(HttpStatusCode.BadRequest, scan.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, search.StatusCode);
    }

    [Fact]
    public async Task Tenant_b_in_header_is_rejected()
    {
        var a = await _client.LoginAsync(_data.A, "cashier");
        var request = ApiClient.Get($"/products/barcode/{_data.B.Barcode}", a.AccessToken);
        request.Headers.Add("X-Tenant-Id", _data.B.Id.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_b_product_id_in_path_is_not_found()
    {
        var a = await _client.LoginAsync(_data.A, "owner");

        var response = await _client.SendAsync(ApiClient.Get($"/products/{_data.B.ProductId}", a.AccessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_never_returns_other_tenant_products()
    {
        // Оба товара содержат суффикс в названии; A должен увидеть только свой.
        var a = await _client.LoginAsync(_data.A, "cashier");

        var response = await _client.SendAsync(ApiClient.Get($"/products/search?q={Uri.EscapeDataString(_data.Suffix)}", a.AccessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.JsonAsync()).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(_data.A.ProductId, ids);
        Assert.DoesNotContain(_data.B.ProductId, ids);
    }

    private static async Task<bool> ProductExistsInAnyTenantAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM products WHERE id = $1)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
