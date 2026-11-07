using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KassirERP.Api.Tests.Infrastructure;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Отчёты (шаг 7): продажи — на филиале 3 тенанта A (там в тестах больше никто не продаёт),
/// сроки годности — на филиале 2.
/// Касса филиала 3 регистрируется владельцем (вход с выбором филиала 3), кассир
/// без филиалов получает доступ к филиалу 3 через API на время теста.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReportTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Reports_show_sales_profit_shifts_mismatches_late_returns_and_stock()
    {
        var owner = (await _client.LoginAsync(_data.A, "owner", _data.A.Store3Id)).AccessToken;
        var store = _data.A.Store3Id;
        var previousList = await StoreListAsync(store);
        var user = _data.A.Users["nostore"];
        try
        {
            await Send(HttpMethod.Put, $"/users/{user.Id}/stores", owner, new { storeIds = new[] { store } });
            var kassa = await NewKassaAsync(owner, user);
            var kassa2 = await NewKassaAsync(owner, user);

            // Товар: себестоимость 100, базовая цена 200; 10 шт в филиале 3. Лист филиала 3 — 250.
            var (product, variant) = await NewGoodsAsync(owner, qty: 10m);
            var (_, scarce) = await NewGoodsAsync(owner, qty: 1m);
            var list = Guid.NewGuid();
            await Send(HttpMethod.Post, "/price-lists", owner, new { id = list, name = $"Отчёты {_data.Suffix}" });
            await Send(HttpMethod.Put, $"/price-lists/{list}/items", owner, new { items = new[] { new { variantId = variant, salePrice = 250m } } });
            await Send(HttpMethod.Put, $"/stores/{store}/price-list", owner, new { priceListId = list });

            // Смена 1: продажи 2 и 1 шт по 200 (на 50 ниже листа), возврат 1 шт; 3 шт товара, которого 1 — минус 2.
            var shift = Guid.NewGuid();
            var s1 = Sale(shift, 1, variant, 2);
            var s2 = Sale(shift, 2, variant, 1);
            var minus = Sale(shift, 3, scarce, 3);
            var r1 = Return(shift, 4, s1, 1);
            await PushAsync(kassa, new
            {
                shifts = new[] { new { id = shift, openingCash = 1000m } },
                sales = new object[] { s1, s2, minus, r1 },
                closes = new[] { new { shiftId = shift, closingCash = 1990m } },
            });

            // Поздний возврат больше проданного с кассы 2: продажа на кассе 2, два возврата той же пары
            // на кассе 1 в уже закрытой смене (второй — на разбор).
            var shift2 = Guid.NewGuid();
            var other = Sale(shift2, 1, variant, 1);
            var lateA = Return(shift, 5, other, 1);
            var lateB = Return(shift, 6, other, 1);
            await PushAsync(kassa, new { sales = new[] { lateA, lateB } });                    // исходного ещё нет — retry
            await PushAsync(kassa2, new { shifts = new[] { new { id = shift2, openingCash = 0m } }, sales = new[] { other } });
            await PushAsync(kassa, new { sales = new[] { lateA, lateB } });

            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6));                    // Asia/Bishkek
            var period = $"from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}&storeId={store}";
            var sales = await GetAsync($"/reports/sales?{period}", owner);
            var mismatches = await GetAsync($"/reports/price-mismatches?{period}", owner);
            var shifts = await GetAsync($"/reports/shifts?{period}", owner);
            var changed = await GetAsync($"/reports/shifts?{period}&changedOnly=true", owner);
            var review = await GetAsync($"/reports/late-returns?{period}", owner);
            var stock = await DrainAsync($"/reports/stock?storeId={store}&limit=1", owner);
            var negative = await GetAsync($"/reports/negative-stock?storeId={store}", owner);

            // Продажи: 4 чека продажи (2 + 1 + 3 шт «в минус» + 1 на кассе 2) и 3 возврата.
            var total = sales.GetProperty("total");
            Assert.Equal(4, total.GetProperty("sales").GetInt32());
            Assert.Equal(3, total.GetProperty("returns").GetInt32());
            Assert.Equal(1400m, total.GetProperty("revenue").GetDecimal());         // 400 + 200 + 600 + 200
            Assert.Equal(600m, total.GetProperty("refunds").GetDecimal());          // 3 × 200
            Assert.Equal(800m, total.GetProperty("net").GetDecimal());
            Assert.Equal(400m, total.GetProperty("cost").GetDecimal());             // (7 − 3) шт × 100
            Assert.Equal(400m, total.GetProperty("profit").GetDecimal());
            Assert.All(sales.GetProperty("days").EnumerateArray(), d => Assert.Equal(store, d.GetProperty("storeId").GetGuid()));

            // Расхождения: строки пары из листа (250), пробитые по 200: 2 + 1 + 1 шт.
            Assert.Equal(3, mismatches.GetProperty("lines").GetInt32());
            Assert.Equal(200m, mismatches.GetProperty("difference").GetDecimal()); // 4 шт × 50

            // Смена 1: ожидалось 1000 + 1200 − 200 = 2000 при закрытии, кассир насчитал 1990;
            // два поздних возврата по 200 → 1600, смена помечена, 2 поздних документа.
            var closed = shifts.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == shift);
            Assert.Equal(1600m, closed.GetProperty("expectedCash").GetDecimal());
            Assert.Equal(1990m, closed.GetProperty("closingCash").GetDecimal());
            Assert.Equal(390m, closed.GetProperty("difference").GetDecimal());
            Assert.True(closed.GetProperty("changedAfterClose").GetBoolean());
            Assert.Equal(2, closed.GetProperty("lateDocuments").GetInt32());
            Assert.Equal([shift], changed.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));

            var flagged = Assert.Single(review.EnumerateArray());
            Assert.Equal(lateB.Id, flagged.GetProperty("saleId").GetGuid());
            Assert.Contains("больше проданного", flagged.GetProperty("reviewReason").GetString());

            // Остатки филиала 3 постранично: 10 − 3 + 1 − 1 + 2 = 9 и 1 − 3 = −2.
            Assert.Equal(9m, stock.Single(s => s.GetProperty("variantId").GetGuid() == variant).GetProperty("qty").GetDecimal());
            var neg = Assert.Single(negative.GetProperty("items").EnumerateArray());
            Assert.Equal((scarce, -2m), (neg.GetProperty("variantId").GetGuid(), neg.GetProperty("qty").GetDecimal()));
            _ = product;
        }
        finally
        {
            await Send(HttpMethod.Put, $"/users/{user.Id}/stores", owner, new { storeIds = Array.Empty<Guid>() });
            await AdminExecAsync("UPDATE stores SET price_list_id = $2 WHERE id = $1", store, previousList);
        }
    }

    [Fact]
    public async Task Expiring_report_counts_batches_first_expired_first_out()
    {
        var owner = (await _client.LoginAsync(_data.A, "owner", _data.A.Store2Id)).AccessToken;
        var user = _data.A.Users["nostore"];
        try
        {
            await Send(HttpMethod.Put, $"/users/{user.Id}/stores", owner, new { storeIds = new[] { _data.A.Store2Id } });
            var kassa = await NewKassaAsync(owner, user, _data.A.Store2Id);
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6));

            // Две партии по 5: срок через 3 и через 20 дней. Продано 2 — осталось 8: в поздней партии 5, в ранней 3.
            var mild = await NewExpiringGoodsAsync(owner, today.AddDays(3), today.AddDays(20));
            // То же, но продано 6 — осталось 4, все в поздней партии: ранняя уже распродана.
            var sold = await NewExpiringGoodsAsync(owner, today.AddDays(3), today.AddDays(20));
            var shift = Guid.NewGuid();
            await PushAsync(kassa, new
            {
                shifts = new[] { new { id = shift, openingCash = 0m } },
                sales = new[] { Sale(shift, 1, mild, 2), Sale(shift, 2, sold, 6) },
            });

            var week = await DrainAsync($"/reports/expiring?storeId={_data.A.Store2Id}&days=7&limit=1", owner);
            var month = await DrainAsync($"/reports/expiring?storeId={_data.A.Store2Id}&days=30", owner);

            Assert.Equal([(mild, 3, 3m)], Rows(week, mild, sold));                             // через 3 дня истекут 3 шт
            Assert.Equal([(mild, 3, 3m), (mild, 20, 5m), (sold, 20, 4m)], Rows(month, mild, sold));
        }
        finally
        {
            await Send(HttpMethod.Put, $"/users/{user.Id}/stores", owner, new { storeIds = Array.Empty<Guid>() });
        }

        static List<(Guid, int, decimal)> Rows(List<JsonElement> items, Guid a, Guid b) =>
            items.Where(i => i.GetProperty("variantId").GetGuid() is var v && (v == a || v == b))
                .Select(i => (i.GetProperty("variantId").GetGuid(), i.GetProperty("daysLeft").GetInt32(), i.GetProperty("qty").GetDecimal()))
                .OrderBy(r => r.Item2).ThenBy(r => r.Item1 == a ? 0 : 1).ToList();
    }

    [Fact]
    public async Task Cashier_gets_403_keeper_sees_only_stock_and_bad_requests_are_refused()
    {
        var cashier = (await _client.LoginAsync(_data.A, "cashier")).AccessToken;
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var owner = (await _client.LoginAsync(_data.A, "owner")).AccessToken;
        var period = "from=2026-01-01&to=2026-01-31";
        string[] finance = [$"/reports/sales?{period}", $"/reports/price-mismatches?{period}", $"/reports/shifts?{period}", $"/reports/late-returns?{period}"];
        string[] stock = ["/reports/stock", "/reports/expiring", "/reports/negative-stock"];

        foreach (var url in finance.Concat(stock))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, url, cashier)).StatusCode);
        }

        foreach (var url in finance)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, url, keeper)).StatusCode);
        }

        foreach (var url in stock)
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, url, keeper)).StatusCode);   // филиал кладовщика из токена
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/reports/sales", owner)).StatusCode);                          // нет периода
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/reports/sales?from=2025-01-01&to=2026-12-31", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/reports/stock", owner)).StatusCode);                          // у владельца нет филиала
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/reports/sales?{period}&storeId={_data.B.StoreId}", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/reports/stock?storeId={_data.B.StoreId}", owner)).StatusCode);
    }

    // -----------------------------------------------------------------------

    private static SaleRequest Sale(Guid shift, int number, Guid variant, decimal qty) =>
        new(Guid.NewGuid(), shift, number, DateTimeOffset.UtcNow, [new SaleItem(Guid.NewGuid(), variant, qty, 200m, 0m, null, null)],
            [new Payment(Guid.NewGuid(), "cash", 200m * qty)]);

    private static SaleRequest Return(Guid shift, int number, SaleRequest original, decimal qty) =>
        new(Guid.NewGuid(), shift, number, DateTimeOffset.UtcNow,
            [new SaleItem(Guid.NewGuid(), null, qty, null, 0m, null, original.Items[0].Id)], [new Payment(Guid.NewGuid(), "cash", 200m * qty)],
            "return", original.Id);

    /// <summary>Касса филиала из токена владельца; кассир входит по PIN.</summary>
    private async Task<string> NewKassaAsync(string owner, TestUser cashier, Guid? store = null)
    {
        var code = (await (await Send(HttpMethod.Post, "/devices/enrollment-codes", owner, new { })).JsonAsync()).GetProperty("code").GetString();
        var enrolled = await (await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса отчётов" })).JsonAsync();
        var device = new TestDevice(enrolled.GetProperty("deviceId").GetGuid(), enrolled.GetProperty("deviceSecret").GetString()!, store ?? _data.A.Store3Id);
        var pin = await _client.PinAsync(device, cashier.Login, cashier.Pin!);
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        return (await pin.JsonAsync()).GetProperty("accessToken").GetString()!;
    }

    private async Task<(Guid Product, Guid Variant)> NewGoodsAsync(string owner, decimal qty)
    {
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/products", owner, new
        {
            id = product, name = $"Отчёт {_data.Suffix} {product:N}",
            variants = new[] { new { id = variant, costPrice = 100m, salePrice = 200m } },
        })).EnsureSuccessStatusCode();
        await AcceptPurchaseAsync(owner, [new { id = Guid.NewGuid(), variantId = variant, qty, costPrice = 100m }]);
        return (product, variant);
    }

    private async Task<Guid> NewExpiringGoodsAsync(string owner, DateOnly early, DateOnly late)
    {
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/products", owner, new
        {
            id = product, name = $"Срок {_data.Suffix} {product:N}",
            variants = new[] { new { id = variant, costPrice = 100m, salePrice = 200m, tracksExpiry = true } },
        })).EnsureSuccessStatusCode();
        await AcceptPurchaseAsync(owner,
        [
            new { id = Guid.NewGuid(), variantId = variant, qty = 5m, costPrice = 100m, expiresOn = early },
            new { id = Guid.NewGuid(), variantId = variant, qty = 5m, costPrice = 100m, expiresOn = late },
        ]);
        return variant;
    }

    private async Task AcceptPurchaseAsync(string owner, object[] items)
    {
        var purchase = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/purchases", owner, new { id = purchase, items })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Post, $"/purchases/{purchase}/accept", owner)).EnsureSuccessStatusCode();
    }

    private async Task PushAsync(string kassa, object batch) =>
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/sync/push", kassa, batch)).StatusCode);

    private async Task<JsonElement> GetAsync(string url, string token)
    {
        var response = await Send(HttpMethod.Get, url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    /// <summary>Все страницы отчёта подряд.</summary>
    private async Task<List<JsonElement>> DrainAsync(string url, string token)
    {
        var items = new List<JsonElement>();
        string? next = null;
        do
        {
            var page = await GetAsync(next is null ? url : $"{url}&after={Uri.EscapeDataString(next)}", token);
            items.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.Clone()));
            next = page.GetProperty("next").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next").GetString();
        }
        while (next is not null);

        return items;
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

    private static async Task<Guid?> StoreListAsync(Guid store)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT price_list_id FROM stores WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = store });
        return await command.ExecuteScalarAsync() as Guid?;
    }

    private static async Task AdminExecAsync(string sql, Guid id, Guid? value)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await command.ExecuteNonQueryAsync();
    }
}
