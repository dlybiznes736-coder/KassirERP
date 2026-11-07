using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;
using KassirERP.Application.Catalog;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Продажа (шаг 5): чек со штучным и весовым товаром, оплаты, возврат.
/// Каждый тест — своя касса (подключена через менеджера), своя смена и свои
/// товары с приходом: остатки не зависят от других тестов.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SaleTests(KassirApiFactory factory)
{
    private static int _plu = 60000;
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Sale_with_piece_and_weighted_goods_and_mixed_payment_decreases_stock()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();

        // Кроссовки 1 шт × 4 990 + колбаса 1.250 кг × 520 = 4 990 + 650 = 5 640; наличные 1 000 + карта 4 640.
        var response = await PostSale(kassa, Sale(kassa,
            [Piece(goods, 1), Weighted(goods, 1.250m)],
            [Pay("cash", 1000m), Pay("card", 4640m)]));
        var sale = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(5640.00m, sale.GetProperty("total").GetDecimal());
        Assert.Equal("kg", sale.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("variantId").GetGuid() == goods.WeightedId).GetProperty("unit").GetString());
        Assert.Equal(9m, await StockAsync(goods.ShoeProductId, goods.ShoeId));
        Assert.Equal(18.75m, await StockAsync(goods.WeightedProductId, goods.WeightedId));
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("card")]
    public async Task Single_payment_methods_work(string method)
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();

        var response = await PostSale(kassa, Sale(kassa, [Piece(goods, 2)], [Pay(method, 9980m)]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Payments_must_add_up_to_the_total()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();

        var underpaid = await PostSale(kassa, Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4000m)]));
        var fractionalPiece = await PostSale(kassa, Sale(kassa, [Piece(goods, 1.5m)], [Pay("cash", 7485m)]));

        Assert.Equal(HttpStatusCode.BadRequest, underpaid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fractionalPiece.StatusCode);  // штучный — целым числом
        Assert.Equal(10m, await StockAsync(goods.ShoeProductId, goods.ShoeId));
    }

    [Fact]
    public async Task Resending_the_same_sale_does_not_create_a_duplicate()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        var sale = Sale(kassa, [Piece(goods, 1), Weighted(goods, 0.5m)], [Pay("cash", 5250m)]);

        var first = await PostSale(kassa, sale);
        var second = await PostSale(kassa, sale);
        var third = await PostSale(kassa, sale);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal((1L, 2L, 1L, 2L), await CountsAsync(sale.Id));   // чек, строки, оплаты, движения
        Assert.Equal(9m, await StockAsync(goods.ShoeProductId, goods.ShoeId));
        Assert.Equal(19.5m, await StockAsync(goods.WeightedProductId, goods.WeightedId));
    }

    [Fact]
    public async Task Posted_sale_does_not_change()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        var sale = Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4990m)]);
        await PostSale(kassa, sale);

        // Тот же id с другим содержимым — отказ, в базе — прежний чек.
        var changed = await PostSale(kassa, sale with { Items = [Piece(goods, 3) with { Id = sale.Items[0].Id }], Payments = [Pay("cash", 14970m)] });
        // Тот же номер чека на кассе, другой id — отказ.
        var sameNumber = await PostSale(kassa, Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4990m)]) with { LocalNumber = sale.LocalNumber });
        var stored = await (await Send(HttpMethod.Get, $"/sales/{sale.Id}", kassa.Token, null)).JsonAsync();

        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameNumber.StatusCode);
        Assert.Equal(1m, stored.GetProperty("items")[0].GetProperty("qty").GetDecimal());
        Assert.Equal(9m, await StockAsync(goods.ShoeProductId, goods.ShoeId));

        // И сама база не даёт изменить или удалить проведённый чек — даже роли приложения.
        foreach (var sql in new[]
                 {
                     "UPDATE sales SET total = 1 WHERE id = $1",
                     "DELETE FROM sales WHERE id = $1",
                     "UPDATE sale_items SET qty = 100 WHERE sale_id = $1",
                     "DELETE FROM sale_items WHERE sale_id = $1",
                     "UPDATE payments SET amount = 1 WHERE sale_id = $1",
                 })
        {
            Assert.True(await FailsAsAppAsync(sql, sale.Id), $"должно быть запрещено: {sql}");
        }
    }

    [Fact]
    public async Task Return_more_than_sold_is_refused_and_returns_add_stock_back()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        var sale = Sale(kassa, [Weighted(goods, 1.250m)], [Pay("cash", 650m)]);
        await PostSale(kassa, sale);
        var line = sale.Items[0].Id;

        var r1 = await PostSale(kassa, Return(kassa, sale.Id, [(line, 0.500m)], 260.00m));   // осталось 0.750
        var tooMuch = await PostSale(kassa, Return(kassa, sale.Id, [(line, 0.800m)], 416.00m));
        var r2 = await PostSale(kassa, Return(kassa, sale.Id, [(line, 0.750m)], 390.00m));   // осталось 0
        var oneGram = await PostSale(kassa, Return(kassa, sale.Id, [(line, 0.001m)], 0.52m));

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal("return", (await r1.JsonAsync()).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oneGram.StatusCode);
        Assert.Equal(20m, await StockAsync(goods.WeightedProductId, goods.WeightedId));   // 20 − 1.25 + 0.5 + 0.75
    }

    [Fact]
    public async Task Parallel_returns_cannot_exceed_what_was_sold()
    {
        var kassa = await NewKassaAsync();
        var kassa2 = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        var sale = Sale(kassa, [Weighted(goods, 1.000m)], [Pay("cash", 520m)]);
        await PostSale(kassa, sale);
        var line = sale.Items[0].Id;

        // Две кассы одновременно возвращают по 0.7 кг из проданного 1 кг.
        var results = await Task.WhenAll(
            PostSale(kassa, Return(kassa, sale.Id, [(line, 0.700m)], 364.00m)),
            PostSale(kassa2, Return(kassa2, sale.Id, [(line, 0.700m)], 364.00m)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Return_refunds_price_and_proportional_discount_of_the_original_line()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        // 2 пары × 4 990 = 9 980, скидка 980 → 9 000. Возврат одной пары: 4 990 − 490 = 4 500.
        var sale = Sale(kassa, [Piece(goods, 2) with { Discount = 980m }], [Pay("card", 9000m)]);
        await PostSale(kassa, sale);

        var wrongRefund = await PostSale(kassa, Return(kassa, sale.Id, [(sale.Items[0].Id, 1m)], 4990m));
        var refund = await PostSale(kassa, Return(kassa, sale.Id, [(sale.Items[0].Id, 1m)], 4500m, "card"));

        Assert.Equal(HttpStatusCode.BadRequest, wrongRefund.StatusCode);
        Assert.Equal(HttpStatusCode.Created, refund.StatusCode);
        Assert.Equal(4500m, (await refund.JsonAsync()).GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Return_line_must_come_from_the_original_sale()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        var saleA = Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4990m)]);
        var saleB = Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4990m)]);
        await PostSale(kassa, saleA);
        await PostSale(kassa, saleB);

        var mixed = await PostSale(kassa, Return(kassa, saleA.Id, [(saleB.Items[0].Id, 1m)], 4990m));

        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
    }

    [Fact]
    public async Task Sales_need_a_device_token_an_open_shift_and_own_goods()
    {
        var kassa = await NewKassaAsync();
        var goods = await NewGoodsAsync();
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;   // вход по паролю, без кассы

        var noDevice = await Send(HttpMethod.Post, "/sales", keeper, Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4990m)]));
        var foreign = await PostSale(kassa, Sale(kassa,
            [new SaleItem(Guid.NewGuid(), _data.B.VariantId, 1m, 4990m, 0m, null, null)], [Pay("cash", 4990m)]));   // пара фирмы B
        var close = await Send(HttpMethod.Post, $"/shifts/{kassa.ShiftId}/close", kassa.Token, new { closingCash = 1000m });
        var afterClose = await PostSale(kassa, Sale(kassa, [Piece(goods, 1)], [Pay("cash", 4990m)]));

        Assert.Equal(HttpStatusCode.Forbidden, noDevice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, afterClose.StatusCode);
    }

    [Fact]
    public async Task Closing_a_shift_counts_expected_cash()
    {
        var kassa = await NewKassaAsync(openingCash: 1000m);
        var goods = await NewGoodsAsync();
        var sale = Sale(kassa, [Piece(goods, 1), Weighted(goods, 1.250m)], [Pay("cash", 2000m), Pay("card", 3640m)]);
        await PostSale(kassa, sale);
        await PostSale(kassa, Return(kassa, sale.Id, [(sale.Items[1].Id, 0.250m)], 130.00m));

        var close = await (await Send(HttpMethod.Post, $"/shifts/{kassa.ShiftId}/close", kassa.Token, new { closingCash = 2870m })).JsonAsync();

        // 1 000 на начало + 2 000 наличными − 130 возврат наличными = 2 870
        Assert.Equal("closed", close.GetProperty("status").GetString());
        Assert.Equal(2870m, close.GetProperty("expectedCash").GetDecimal());
    }

    // -----------------------------------------------------------------------

    private sealed record Kassa(string Token, Guid DeviceId, Guid ShiftId)
    {
        private int _number;
        public int NextNumber() => Interlocked.Increment(ref _number);
    }

    private sealed record Goods(Guid ShoeProductId, Guid ShoeId, Guid WeightedProductId, Guid WeightedId, string Plu);

    /// <summary>Новая касса филиала 1: менеджер выдаёт код, касса регистрируется, кассир входит по PIN, смена открыта.</summary>
    private async Task<Kassa> NewKassaAsync(decimal openingCash = 0m)
    {
        var manager = await _client.LoginAsync(_data.A, "manager");
        var code = (await (await Send(HttpMethod.Post, "/devices/enrollment-codes", manager.AccessToken, new { })).JsonAsync()).GetProperty("code").GetString();
        var enrolled = await (await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса теста продаж" })).JsonAsync();
        var device = new TestDevice(enrolled.GetProperty("deviceId").GetGuid(), enrolled.GetProperty("deviceSecret").GetString()!, _data.A.StoreId);
        var cashier = _data.A.Users["cashier"];
        var token = (await (await _client.PinAsync(device, cashier.Login, cashier.Pin!)).JsonAsync()).GetProperty("accessToken").GetString()!;
        var shiftId = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/shifts", token, new { id = shiftId, openingCash })).EnsureSuccessStatusCode();
        return new Kassa(token, device.Id, shiftId);
    }

    /// <summary>Кроссовки (шт, 4 990) — 10 шт и весовая колбаса (кг, 520) — 20 кг, приходом в филиал 1.</summary>
    private async Task<Goods> NewGoodsAsync()
    {
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var plu = Interlocked.Increment(ref _plu).ToString("D5");
        var goods = new Goods(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), plu);

        (await Send(HttpMethod.Post, "/products", keeper, new
        {
            id = goods.ShoeProductId, name = $"Кроссовки продажа {_data.Suffix}",
            variants = new[] { new { id = goods.ShoeId, size = "42", color = "белый", costPrice = 2500m, salePrice = 4990m } },
        })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Post, "/products", keeper, new
        {
            id = goods.WeightedProductId, name = $"Колбаса продажа {_data.Suffix}",
            variants = new[] { new { id = goods.WeightedId, unit = "kg", plu, costPrice = 300m, salePrice = 520m } },
        })).EnsureSuccessStatusCode();

        var purchase = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = purchase,
            items = new[]
            {
                new { id = Guid.NewGuid(), variantId = goods.ShoeId, qty = 10m, costPrice = 2500m },
                new { id = Guid.NewGuid(), variantId = goods.WeightedId, qty = 20m, costPrice = 300m },
            },
        })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Post, $"/purchases/{purchase}/accept", keeper, null)).EnsureSuccessStatusCode();
        return goods;
    }

    private static SaleRequest Sale(Kassa kassa, SaleItem[] items, Payment[] payments) =>
        new(Guid.NewGuid(), kassa.ShiftId, kassa.NextNumber(), DateTimeOffset.UtcNow, items, payments);

    private static SaleRequest Return(Kassa kassa, Guid originalId, (Guid Line, decimal Qty)[] lines, decimal refund, string method = "cash") =>
        new(Guid.NewGuid(), kassa.ShiftId, kassa.NextNumber(), DateTimeOffset.UtcNow,
            lines.Select(l => new SaleItem(Guid.NewGuid(), null, l.Qty, null, 0m, null, l.Line)).ToArray(),
            [Pay(method, refund)], "return", originalId);

    private static SaleItem Piece(Goods goods, decimal qty) => new(Guid.NewGuid(), goods.ShoeId, qty, 4990m, 0m, null, null);

    private static SaleItem Weighted(Goods goods, decimal kg)
    {
        var first12 = "22" + goods.Plu + ((int)(kg * 1000)).ToString("D5");
        return new SaleItem(Guid.NewGuid(), goods.WeightedId, kg, 520m, 0m, first12 + WeightBarcode.CheckDigit(first12), null);
    }

    private static Payment Pay(string method, decimal amount) => new(Guid.NewGuid(), method, amount);

    private Task<HttpResponseMessage> PostSale(Kassa kassa, SaleRequest sale) => Send(HttpMethod.Post, "/sales", kassa.Token, sale);

    private async Task<decimal> StockAsync(Guid productId, Guid variantId)
    {
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var product = await (await Send(HttpMethod.Get, $"/products/{productId}", keeper, null)).JsonAsync();
        return product.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == variantId).GetProperty("stock").GetDecimal();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<(long Sales, long Items, long Payments, long Movements)> CountsAsync(Guid saleId)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM sales WHERE id = $1), (SELECT count(*) FROM sale_items WHERE sale_id = $1),
                   (SELECT count(*) FROM payments WHERE sale_id = $1), (SELECT count(*) FROM stock_movements WHERE doc_id = $1)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = saleId });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>Выполняет SQL ролью приложения в тенанте A; true — база отказала.</summary>
    private async Task<bool> FailsAsAppAsync(string sql, Guid saleId)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, tx))
        {
            set.Parameters.Add(new NpgsqlParameter { Value = _data.A.Id.ToString() });
            await set.ExecuteNonQueryAsync();
        }

        try
        {
            await using var command = new NpgsqlCommand(sql, connection, tx);
            command.Parameters.Add(new NpgsqlParameter { Value = saleId });
            await command.ExecuteNonQueryAsync();
            return false;
        }
        catch (PostgresException)
        {
            return true;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }
}

// Тело запроса /sales в тестах (поля — как в API).
internal sealed record SaleItem(Guid Id, Guid? VariantId, decimal Qty, decimal? Price, decimal Discount, string? ScannedCode, Guid? ReturnOfItemId);

internal sealed record Payment(Guid Id, string Method, decimal Amount);

internal sealed record SaleRequest(
    Guid Id, Guid ShiftId, int LocalNumber, DateTimeOffset CreatedAtLocal, SaleItem[] Items, Payment[] Payments,
    string Type = "sale", Guid? ReturnOfSaleId = null);
