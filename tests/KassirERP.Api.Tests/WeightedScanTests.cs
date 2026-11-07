using System.Net;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;
using KassirERP.Application.Catalog;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>Весовой товар: штрихкод 20–29 (PLU + вес), размер и цвет необязательны.</summary>
[Collection(ApiCollection.Name)]
public sealed class WeightedScanTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>EAN-13 «PP CCCCC WWWWW K» с верной контрольной цифрой.</summary>
    private static string WeightCode(string prefix, string plu, int grams)
    {
        var first12 = prefix + plu + grams.ToString("D5");
        return first12 + WeightBarcode.CheckDigit(first12);
    }

    [Fact]
    public async Task Ordinary_barcode_scans_as_one_piece()
    {
        var body = await ScanAsync(_data.A.Barcode, HttpStatusCode.OK);

        Assert.Equal("pcs", body!.Value.GetProperty("unit").GetString());
        Assert.Equal(1m, body.Value.GetProperty("quantity").GetDecimal());
        Assert.Equal(4990m, body.Value.GetProperty("lineTotal").GetDecimal());
        Assert.Equal("42", body.Value.GetProperty("size").GetString());
    }

    [Fact]
    public async Task Weight_barcode_gives_kilograms_price_and_stock()
    {
        // 22 01234 01250 K → PLU 01234, 1.250 кг × 520.00 = 650.00
        var code = WeightCode("22", _data.A.Plu, 1250);

        var body = (await ScanAsync(code, HttpStatusCode.OK))!.Value;

        Assert.Equal(_data.A.WeightedVariantId, body.GetProperty("variantId").GetGuid());
        Assert.Equal("kg", body.GetProperty("unit").GetString());
        Assert.Equal(1.250m, body.GetProperty("quantity").GetDecimal());
        Assert.Equal(520.00m, body.GetProperty("salePrice").GetDecimal());
        Assert.Equal(650.00m, body.GetProperty("lineTotal").GetDecimal());
        Assert.Equal(_data.A.WeightedStock, body.GetProperty("stock").GetDecimal());
        Assert.Equal(code, body.GetProperty("scannedCode").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("size").ValueKind);
    }

    [Fact]
    public async Task Weight_barcode_with_a_few_grams()
    {
        // 0.333 кг × 520.00 = 173.16, префикс 29 — тоже весовой по умолчанию
        var body = (await ScanAsync(WeightCode("29", _data.A.Plu, 333), HttpStatusCode.OK))!.Value;

        Assert.Equal(0.333m, body.GetProperty("quantity").GetDecimal());
        Assert.Equal(173.16m, body.GetProperty("lineTotal").GetDecimal());
    }

    [Theory]
    [InlineData("99999")]   // PLU нет ни у кого
    [InlineData("05555")]   // PLU есть, но у фирмы B
    public async Task Weight_barcode_of_unknown_product_is_not_found(string plu)
    {
        await ScanAsync(WeightCode("22", plu, 1250), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Weight_barcode_with_wrong_check_digit_is_not_treated_as_weight()
    {
        var good = WeightCode("22", _data.A.Plu, 1250);
        var bad = good[..12] + (char)('0' + (good[12] - '0' + 1) % 10);

        await ScanAsync(bad, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_prefixes_given_to_scales_are_weight_codes()
    {
        await AdminAsync("INSERT INTO weight_barcode_prefixes (tenant_id, prefix) VALUES ($1, '21')");
        try
        {
            await ScanAsync(WeightCode("21", _data.A.Plu, 500), HttpStatusCode.OK);
            await ScanAsync(WeightCode("22", _data.A.Plu, 500), HttpStatusCode.NotFound);
        }
        finally
        {
            await AdminAsync("DELETE FROM weight_barcode_prefixes WHERE tenant_id = $1");
        }
    }

    [Fact]
    public async Task Grocery_product_without_size_and_color_is_created_and_has_one_such_variant()
    {
        var keeper = await _client.LoginAsync(_data.A, "keeper");
        var productId = Guid.NewGuid();
        var create = await Post("/products", keeper.AccessToken, new
        {
            id = productId,
            name = $"Сыр весовой {_data.Suffix}",
            variants = new[] { new { id = Guid.NewGuid(), unit = "kg", plu = "04321", costPrice = 400m, salePrice = 690m } },
        });
        var secondPlain = await Post($"/products/{productId}/variants", keeper.AccessToken, new
        {
            variants = new[] { new { id = Guid.NewGuid(), unit = "kg", costPrice = 400m, salePrice = 690m } },
        });
        var pluOnPiece = await Post("/products", keeper.AccessToken, new
        {
            id = Guid.NewGuid(),
            name = "Неверно",
            variants = new[] { new { id = Guid.NewGuid(), unit = "pcs", plu = "04322", costPrice = 1m, salePrice = 1m } },
        });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondPlain.StatusCode);   // вторая пара без размера и цвета
        Assert.Equal(HttpStatusCode.BadRequest, pluOnPiece.StatusCode);  // PLU — только у весового
        await ScanAsync(WeightCode("23", "04321", 2000), HttpStatusCode.OK);
    }

    private async Task<System.Text.Json.JsonElement?> ScanAsync(string code, HttpStatusCode expected)
    {
        var cashier = await _client.LoginAsync(_data.A, "cashier");
        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{code}", cashier.AccessToken));
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.OK ? await response.JsonAsync() : null;
    }

    private Task<HttpResponseMessage> Post(string url, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task AdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = _data.A.Id });
        await command.ExecuteNonQueryAsync();
    }
}
