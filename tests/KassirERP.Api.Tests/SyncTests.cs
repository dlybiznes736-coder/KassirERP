using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KassirERP.Api.Tests.Infrastructure;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Синхронизация (шаг 6). Касса — новая касса филиала 1, без открытой смены:
/// смену она открывает в пачке, как настоящая касса после работы без связи.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SyncTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Each_sale_in_the_batch_is_accepted_or_rejected_separately()
    {
        var kassa = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync();
        var shift = Guid.NewGuid();
        var good1 = Sale(shift, 1, variant, 1);
        var underpaid = Sale(shift, 2, variant, 1) with { Payments = [Pay(100m)] };           // сумма оплат ≠ сумме чека
        var good2 = Sale(shift, 3, variant, 2);
        var zeroQty = Sale(shift, 4, variant, 1) with { Items = [Line(variant, 0m)] };           // не проходит проверку
        var foreign = Sale(shift, 5, _data.B.VariantId, 1);                                      // пара чужой фирмы
        var sameNumber = Sale(shift, 1, variant, 1);                                             // номер 1 уже занят

        var result = await PushAsync(kassa, new
        {
            shifts = new[] { new { id = shift, openingCash = 1000m } },
            sales = new[] { good1, underpaid, good2, zeroQty, foreign, sameNumber },
            closes = new[] { new { shiftId = shift, closingCash = 1600m } },
        });

        Assert.Equal(["accepted", "rejected", "accepted", "rejected", "rejected", "rejected"], Statuses(result, "sales"));
        Assert.All(result.GetProperty("sales").EnumerateArray().Where(s => s.GetProperty("status").GetString() == "rejected"),
            s => Assert.False(string.IsNullOrEmpty(s.GetProperty("error").GetString())));            // причина у каждого
        Assert.Equal(["accepted"], Statuses(result, "shifts"));
        Assert.Equal(["accepted"], Statuses(result, "closes"));
        Assert.Equal((4, 4), (result.GetProperty("accepted").GetInt32(), result.GetProperty("rejected").GetInt32()));
        Assert.Equal(7m, await StockAsync(product, variant));                                   // 10 − 1 − 2
        var closed = await (await Send(HttpMethod.Get, $"/shifts/{shift}", kassa)).JsonAsync();
        Assert.Equal(1600m, closed.GetProperty("expectedCash").GetDecimal());                    // 1000 + 200 + 400
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Resending_the_same_batch_creates_no_duplicates()
    {
        var kassa = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync();
        var shift = Guid.NewGuid();
        var sales = Enumerable.Range(1, 5).Select(n => Sale(shift, n, variant, 1)).ToArray();
        var refund = Return(shift, 6, sales[0], 1);
        var batch = new
        {
            shifts = new[] { new { id = shift, openingCash = 0m } },
            sales = sales.Append(refund).ToArray(),
            closes = new[] { new { shiftId = shift, closingCash = 800m } },
        };

        var first = await PushAsync(kassa, batch);
        var stockAfterFirst = await StockAsync(product, variant);
        var second = await PushAsync(kassa, batch);
        var third = await PushAsync(kassa, batch);

        Assert.Equal(8, first.GetProperty("accepted").GetInt32());                               // смена, 5 чеков, возврат, закрытие
        Assert.All(new[] { second, third }, r =>
        {
            Assert.Equal(0, r.GetProperty("accepted").GetInt32());
            Assert.Equal(8, r.GetProperty("duplicates").GetInt32());
            Assert.Equal(0, r.GetProperty("rejected").GetInt32());
        });
        Assert.Equal(6m, stockAfterFirst);                                                       // 10 − 5 + 1
        Assert.Equal(6m, await StockAsync(product, variant));
        Assert.Equal((6L, 6L), await CountsAsync(sales.Select(s => s.Id).Append(refund.Id).ToArray()));   // чеков и движений — по одному
    }

    [Fact]
    public async Task Return_that_arrives_before_its_sale_is_handled()
    {
        var kassa = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync();
        var shift = Guid.NewGuid();
        var open = new[] { new { id = shift, openingCash = 0m } };

        // В одной пачке возврат стоит раньше своего чека — сервер разбирает продажи первыми.
        var saleA = Sale(shift, 1, variant, 2);
        var refundA = Return(shift, 2, saleA, 1);
        var together = await PushAsync(kassa, new { shifts = open, sales = new[] { refundA, saleA } });

        // Возврат пришёл пачкой раньше чека: не принят и не отклонён — касса пришлёт ещё раз.
        var saleB = Sale(shift, 3, variant, 3);
        var refundB = Return(shift, 4, saleB, 2);
        var early = await PushAsync(kassa, new { sales = new[] { refundB } });
        var stockWhileWaiting = await StockAsync(product, variant);
        var countsWhileWaiting = await CountsAsync([refundB.Id]);
        var later = await PushAsync(kassa, new { sales = new[] { saleB, refundB } });

        Assert.Equal(["accepted", "accepted"], Statuses(together, "sales"));
        Assert.Equal(["retry"], Statuses(early, "sales"));
        Assert.Equal(9m, stockWhileWaiting);                                                     // 10 − 2 + 1: ранний возврат не тронул остаток
        Assert.Equal((0L, 0L), countsWhileWaiting);                                              // ни чека, ни движения
        Assert.Equal(["accepted", "accepted"], Statuses(later, "sales"));
        Assert.Equal(8m, await StockAsync(product, variant));                                    // 9 − 3 + 2
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Kassa_pulls_catalog_changes_since_its_last_sync()
    {
        var kassa = await NewKassaAsync();

        var (_, old) = await NewGoodsAsync();

        // Первая синхронизация: весь каталог фирмы, страницами по 2.
        var (allVariants, variantCursor) = await DrainAsync(kassa, "/sync/variants", null, limit: 2);
        var (_, productCursor) = await DrainAsync(kassa, "/sync/products", null, limit: 2);

        // После неё: новый товар и новая базовая цена у старой пары.
        var (product, variant) = await NewGoodsAsync();
        await AdminExecAsync("UPDATE product_variants SET sale_price = 222 WHERE id = $1", old);

        var variants = await PollAsync(kassa, "/sync/variants", variantCursor, items => items.Count >= 2);
        var products = await PollAsync(kassa, "/sync/products", productCursor, items => items.Count >= 1);
        var (again, _) = await DrainAsync(kassa, "/sync/variants", variants.Next, limit: 100);

        Assert.Contains(allVariants, v => v.GetProperty("id").GetGuid() == _data.A.VariantId);
        Assert.DoesNotContain(allVariants, v => v.GetProperty("id").GetGuid() == _data.B.VariantId);      // только своя фирма
        Assert.Contains(allVariants, v => v.GetProperty("id").GetGuid() == old);
        Assert.Equal(new[] { variant, old }.Order(), variants.Items.Select(v => v.GetProperty("id").GetGuid()).Order());
        Assert.Equal(222m, variants.Items.Single(v => v.GetProperty("id").GetGuid() == old).GetProperty("basePrice").GetDecimal());
        Assert.Equal([product], products.Items.Select(p => p.GetProperty("id").GetGuid()));
        Assert.Empty(again);                                                                     // с последнего курсора — ничего нового
    }

    [Fact]
    public async Task Kassa_pulls_prices_of_its_own_list_since_its_last_sync()
    {
        var kassa = await NewKassaAsync();
        var manager = (await _client.LoginAsync(_data.A, "manager")).AccessToken;
        var (_, v1) = await NewGoodsAsync();
        var (_, v2) = await NewGoodsAsync();
        var (_, v3) = await NewGoodsAsync();
        var list = Guid.NewGuid();
        var otherList = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/price-lists", manager, new { id = list, name = $"Синхронизация {_data.Suffix}" })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Post, "/price-lists", manager, new { id = otherList, name = $"Чужой лист {_data.Suffix}" })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Put, $"/price-lists/{otherList}/items", manager,
            new { items = new[] { new { variantId = v3, salePrice = 999m } } })).EnsureSuccessStatusCode();
        try
        {
            var noList = await PullAsync(kassa, "/sync/prices", null);
            (await Send(HttpMethod.Put, $"/price-lists/{list}/items", manager,
                new { items = new[] { new { variantId = v1, salePrice = 210m }, new { variantId = v2, salePrice = 220m } } })).EnsureSuccessStatusCode();
            (await Send(HttpMethod.Put, $"/stores/{_data.A.StoreId}/price-list", manager, new { priceListId = list })).EnsureSuccessStatusCode();

            var assigned = await PollAsync(kassa, "/sync/prices", noList.Next, items => items.Count >= 2);
            (await Send(HttpMethod.Put, $"/price-lists/{list}/items", manager,
                new { items = new[] { new { variantId = v1, salePrice = 215m } } })).EnsureSuccessStatusCode();
            (await Send(HttpMethod.Delete, $"/price-lists/{list}/items/{v2}", manager)).EnsureSuccessStatusCode();
            var changed = await PollAsync(kassa, "/sync/prices", assigned.Next, items => items.Count >= 2);
            var quiet = await PullAsync(kassa, "/sync/prices", changed.Next);
            (await Send(HttpMethod.Put, $"/stores/{_data.A.StoreId}/price-list", manager, new { priceListId = (Guid?)null })).EnsureSuccessStatusCode();
            var removed = await PullAsync(kassa, "/sync/prices", quiet.Next);

            Assert.Null(noList.ListId);
            Assert.Empty(noList.Items);
            Assert.Equal(list, assigned.ListId);
            Assert.True(assigned.Reset);                                                         // лист сменился — сбросить и взять с начала
            Assert.Equal([(v1, (decimal?)210m), (v2, 220m)], Prices(assigned).OrderBy(p => p.Item2).ToList());
            Assert.DoesNotContain(Prices(assigned), p => p.Item1 == v3);                         // цены чужого листа не приходят
            Assert.False(changed.Reset);
            Assert.Equal([(v1, (decimal?)215m), (v2, null)], Prices(changed).OrderBy(p => p.Item1 == v1 ? 0 : 1).ToList());
            Assert.Empty(quiet.Items);
            Assert.Null(removed.ListId);
            Assert.True(removed.Reset);                                                          // листа нет — у кассы базовые цены
        }
        finally
        {
            await AdminExecAsync("UPDATE stores SET price_list_id = NULL WHERE id = $1", _data.A.StoreId);
        }
    }

    [Fact]
    public async Task Sync_is_only_for_a_kassa_and_a_broken_cursor_is_refused()
    {
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var kassa = await NewKassaAsync();

        var push = await Send(HttpMethod.Post, "/sync/push", keeper, new { sales = Array.Empty<object>() });
        var pull = await Send(HttpMethod.Get, "/sync/variants", keeper);
        var broken = await Send(HttpMethod.Get, "/sync/variants?after=abc", kassa);
        var tooBig = await Send(HttpMethod.Get, "/sync/variants?limit=100000", kassa);

        Assert.Equal(HttpStatusCode.Forbidden, push.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pull.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
    }

    // -----------------------------------------------------------------------

    private sealed record Page(Guid? ListId, bool Reset, List<JsonElement> Items, string Next, bool HasMore);

    private static SaleItem Line(Guid variant, decimal qty) => new(Guid.NewGuid(), variant, qty, 200m, 0m, null, null);

    private static Payment Pay(decimal amount) => new(Guid.NewGuid(), "cash", amount);

    private static SaleRequest Sale(Guid shift, int number, Guid variant, decimal qty) =>
        new(Guid.NewGuid(), shift, number, DateTimeOffset.UtcNow, [Line(variant, qty)], [Pay(200m * qty)]);

    private static SaleRequest Return(Guid shift, int number, SaleRequest original, decimal qty) =>
        new(Guid.NewGuid(), shift, number, DateTimeOffset.UtcNow,
            [new SaleItem(Guid.NewGuid(), null, qty, null, 0m, null, original.Items[0].Id)], [Pay(200m * qty)], "return", original.Id);

    private static List<string?> Statuses(JsonElement result, string section) =>
        result.GetProperty(section).EnumerateArray().Select(s => s.GetProperty("status").GetString()).ToList();

    private static List<(Guid, decimal?)> Prices(Page page) =>
        page.Items.Select(p => (p.GetProperty("variantId").GetGuid(),
            p.GetProperty("salePrice").ValueKind == JsonValueKind.Null ? (decimal?)null : p.GetProperty("salePrice").GetDecimal())).ToList();

    private async Task<JsonElement> PushAsync(string kassa, object batch)
    {
        var response = await Send(HttpMethod.Post, "/sync/push", kassa, batch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<Page> PullAsync(string kassa, string url, string? after, int? limit = null)
    {
        var query = new List<string>();
        if (after is not null)
        {
            query.Add("after=" + Uri.EscapeDataString(after));
        }

        if (limit is not null)
        {
            query.Add("limit=" + limit);
        }

        var response = await Send(HttpMethod.Get, query.Count == 0 ? url : url + "?" + string.Join("&", query), kassa);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        var list = body.GetProperty("priceListId");
        return new Page(list.ValueKind == JsonValueKind.Null ? null : list.GetGuid(), body.GetProperty("reset").GetBoolean(),
            body.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList(), body.GetProperty("next").GetString()!,
            body.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>Все страницы подряд, пока hasMore.</summary>
    private async Task<(List<JsonElement> Items, string Next)> DrainAsync(string kassa, string url, string? after, int limit)
    {
        var items = new List<JsonElement>();
        var page = await PullAsync(kassa, url, after, limit);
        items.AddRange(page.Items);
        while (page.HasMore)
        {
            page = await PullAsync(kassa, url, page.Next, limit);
            items.AddRange(page.Items);
        }

        return (items, page.Next);
    }

    /// <summary>
    /// Изменение отдаётся, когда закоммичены все транзакции, начатые раньше него
    /// (горизонт). Параллельные тесты других фикстур держат короткие транзакции —
    /// ждём до 10 с, накапливая страницы с одного курсора.
    /// </summary>
    private async Task<Page> PollAsync(string kassa, string url, string after, Func<List<JsonElement>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var items = new List<JsonElement>();
        var cursor = after;
        Page page;
        bool reset = false;
        do
        {
            page = await PullAsync(kassa, url, cursor);
            reset |= page.Reset;
            items.AddRange(page.Items);
            cursor = page.Next;
            if (done(items))
            {
                break;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return page with { Items = items, Reset = reset, Next = cursor };
    }

    /// <summary>Новая касса филиала 1 (без смены: смену открывает пачка).</summary>
    private async Task<string> NewKassaAsync()
    {
        var manager = await _client.LoginAsync(_data.A, "manager");
        var code = (await (await Send(HttpMethod.Post, "/devices/enrollment-codes", manager.AccessToken, new { })).JsonAsync()).GetProperty("code").GetString();
        var enrolled = await (await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса теста синхронизации" })).JsonAsync();
        var device = new TestDevice(enrolled.GetProperty("deviceId").GetGuid(), enrolled.GetProperty("deviceSecret").GetString()!, _data.A.StoreId);
        var cashier = _data.A.Users["cashier"];
        return (await (await _client.PinAsync(device, cashier.Login, cashier.Pin!)).JsonAsync()).GetProperty("accessToken").GetString()!;
    }

    private async Task<(Guid Product, Guid Variant)> NewGoodsAsync()
    {
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/products", keeper, new
        {
            id = product, name = $"Синхр {_data.Suffix} {product:N}",
            variants = new[] { new { id = variant, costPrice = 100m, salePrice = 200m } },
        })).EnsureSuccessStatusCode();
        var purchase = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = purchase, items = new[] { new { id = Guid.NewGuid(), variantId = variant, qty = 10m, costPrice = 100m } },
        })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Post, $"/purchases/{purchase}/accept", keeper)).EnsureSuccessStatusCode();
        return (product, variant);
    }

    private async Task<decimal> StockAsync(Guid product, Guid variant)
    {
        var keeper = (await _client.LoginAsync(_data.A, "keeper")).AccessToken;
        var card = await (await Send(HttpMethod.Get, $"/products/{product}", keeper)).JsonAsync();
        return card.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == variant).GetProperty("stock").GetDecimal();
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

    private static async Task AdminExecAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Сколько из этих чеков в базе и сколько у них движений склада.</summary>
    private static async Task<(long Sales, long Movements)> CountsAsync(Guid[] saleIds)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM sales WHERE id = ANY($1)), (SELECT count(*) FROM stock_movements WHERE doc_id = ANY($1))", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = saleIds });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<bool> StockMatchesJournalAsync()
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT NOT EXISTS (
              SELECT 1 FROM stock s
              FULL JOIN (SELECT tenant_id, store_id, variant_id, sum(qty) q FROM stock_movements
                         WHERE tenant_id = $1 GROUP BY 1, 2, 3) m USING (tenant_id, store_id, variant_id)
              WHERE coalesce(s.tenant_id, m.tenant_id) = $1 AND s.qty IS DISTINCT FROM m.q)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = _data.A.Id });
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
