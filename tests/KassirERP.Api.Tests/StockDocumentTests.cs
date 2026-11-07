using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Списание и инвентаризация. Документы оформляет кладовщик филиала 1; продажи
/// во время инвентаризации — касса того же филиала. Каждый тест — свой товар
/// с приходом 10 шт в филиал 1.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StockDocumentTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Writeoff_with_each_reason_decreases_stock_and_is_not_duplicated()
    {
        var keeper = await KeeperAsync();
        var (product, variant) = await NewGoodsAsync(keeper);
        var spoilage = NewWriteoff("spoilage", variant, 2m, "помята упаковка");
        var expired = NewWriteoff("expired", variant, 1m);
        var defect = NewWriteoff("defect", variant, 3m);

        var codes = new List<HttpStatusCode>();
        foreach (var w in new[] { spoilage, expired, defect, spoilage, expired })   // два последних — повторы
        {
            codes.Add((await Send(HttpMethod.Post, "/writeoffs", keeper, w)).StatusCode);
        }

        var changed = await Send(HttpMethod.Post, "/writeoffs", keeper, spoilage with { reason = "defect" });
        var badReason = await Send(HttpMethod.Post, "/writeoffs", keeper, NewWriteoff("theft", variant, 1m));
        var fractionalPiece = await Send(HttpMethod.Post, "/writeoffs", keeper, NewWriteoff("defect", variant, 0.5m));
        var stored = await (await Send(HttpMethod.Get, $"/writeoffs/{spoilage.id}", keeper)).JsonAsync();

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.OK], codes);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badReason.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fractionalPiece.StatusCode);
        Assert.Equal("spoilage", stored.GetProperty("reason").GetString());
        Assert.Equal("помята упаковка", stored.GetProperty("note").GetString());
        Assert.Equal(100m, stored.GetProperty("items")[0].GetProperty("costPrice").GetDecimal());   // себестоимость скопирована
        Assert.Equal(4m, await StockAsync(keeper, product, variant));                               // 10 − 2 − 1 − 3
        Assert.Equal([-1m, -2m, -3m], await MovementsAsync(variant, "writeoff"));
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
        Assert.True(await FailsAsAppAsync("UPDATE writeoff_items SET qty = 1 WHERE writeoff_id = $1", spoilage.id));
        Assert.True(await FailsAsAppAsync("DELETE FROM writeoffs WHERE id = $1", spoilage.id));
    }

    [Fact]
    public async Task Sale_during_inventory_is_not_lost()
    {
        var keeper = await KeeperAsync();
        var kassa = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync(keeper);
        var inventory = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/inventories", keeper, new { id = inventory })).EnsureSuccessStatusCode();

        // Учёт 10, посчитали 10 (всё на месте). После подсчёта, до проведения, продали 2.
        var counted = await (await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = variant, countedQty = 10m } } })).JsonAsync();
        var sale = await SellAsync(kassa, variant, 2);
        var beforePost = await StockAsync(keeper, product, variant);
        var posted = await (await Send(HttpMethod.Post, $"/inventories/{inventory}/post", keeper)).JsonAsync();
        var afterPost = await StockAsync(keeper, product, variant);

        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
        Assert.Equal(10m, counted.GetProperty("items")[0].GetProperty("bookQty").GetDecimal());
        Assert.Equal(0m, counted.GetProperty("items")[0].GetProperty("difference").GetDecimal());
        Assert.Equal(8m, beforePost);
        Assert.Equal("posted", posted.GetProperty("status").GetString());
        Assert.Equal(8m, afterPost);                                  // продажа не потерялась: не 10
        Assert.Empty(await MovementsAsync(variant, "inventory"));     // расхождения не было — движения нет
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Sales_before_and_after_the_count_with_a_real_shortage()
    {
        var keeper = await KeeperAsync();
        var kassa = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync(keeper);
        var inventory = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/inventories", keeper, new { id = inventory })).EnsureSuccessStatusCode();

        // Инвентаризация открыта, продали 3 (учёт 7); посчитали 6 — недостача 1; потом продали ещё 2.
        (await SellAsync(kassa, variant, 3)).EnsureSuccessStatusCode();
        var counted = await (await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = variant, countedQty = 6m } } })).JsonAsync();
        (await SellAsync(kassa, variant, 2)).EnsureSuccessStatusCode();
        await Send(HttpMethod.Post, $"/inventories/{inventory}/post", keeper);

        Assert.Equal(7m, counted.GetProperty("items")[0].GetProperty("bookQty").GetDecimal());
        Assert.Equal(-1m, counted.GetProperty("items")[0].GetProperty("difference").GetDecimal());
        Assert.Equal(4m, await StockAsync(keeper, product, variant));     // 6 посчитано − 2 продано после подсчёта
        Assert.Equal([-1m], await MovementsAsync(variant, "inventory"));
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Inventory_difference_is_posted_once_and_recount_overwrites()
    {
        var keeper = await KeeperAsync();
        var (shortProduct, shortVariant) = await NewGoodsAsync(keeper);
        var (surplusProduct, surplusVariant) = await NewGoodsAsync(keeper);
        var (sameProduct, sameVariant) = await NewGoodsAsync(keeper);
        var inventory = Guid.NewGuid();

        var opened = await Send(HttpMethod.Post, "/inventories", keeper, new { id = inventory, number = "ИНВ-1" });
        var reopened = await Send(HttpMethod.Post, "/inventories", keeper, new { id = inventory, number = "ИНВ-1" });
        var second = await Send(HttpMethod.Post, "/inventories", keeper, new { id = Guid.NewGuid() });
        await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = shortVariant, countedQty = 9m }, new { variantId = surplusVariant, countedQty = 11m } } });
        var recount = await (await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = shortVariant, countedQty = 7m }, new { variantId = sameVariant, countedQty = 10m } } })).JsonAsync();
        var otherStore = await Send(HttpMethod.Post, $"/inventories/{inventory}/post",
            (await _client.LoginAsync(_data.A, "owner", _data.A.Store2Id)).AccessToken);
        var post1 = await Send(HttpMethod.Post, $"/inventories/{inventory}/post", keeper);
        var post2 = await Send(HttpMethod.Post, $"/inventories/{inventory}/post", keeper);
        var countAfterPost = await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = shortVariant, countedQty = 1m } } });
        var nextOpen = await Send(HttpMethod.Post, "/inventories", keeper, new { id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);          // одна открытая на филиал
        Assert.Equal(3, recount.GetProperty("items").GetArrayLength());     // пересчёт пары не добавил строку
        Assert.Equal(HttpStatusCode.Forbidden, otherStore.StatusCode);
        Assert.Equal(HttpStatusCode.OK, post1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, post2.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, countAfterPost.StatusCode);
        Assert.Equal(HttpStatusCode.Created, nextOpen.StatusCode);          // после проведения можно открыть новую
        Assert.Equal(7m, await StockAsync(keeper, shortProduct, shortVariant));
        Assert.Equal(11m, await StockAsync(keeper, surplusProduct, surplusVariant));
        Assert.Equal(10m, await StockAsync(keeper, sameProduct, sameVariant));
        Assert.Equal([-3m], await MovementsAsync(shortVariant, "inventory"));      // одно движение, по последнему пересчёту
        Assert.Equal([1m], await MovementsAsync(surplusVariant, "inventory"));
        Assert.Empty(await MovementsAsync(sameVariant, "inventory"));
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");

        await Send(HttpMethod.Post, $"/inventories/{(await nextOpen.JsonAsync()).GetProperty("id").GetGuid()}/post", keeper);
    }

    [Fact]
    public async Task Posted_inventory_does_not_change_and_documents_stay_inside_the_tenant()
    {
        var keeper = await KeeperAsync();
        var (_, variant) = await NewGoodsAsync(keeper);
        var inventory = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/inventories", keeper, new { id = inventory })).EnsureSuccessStatusCode();

        var foreignCount = await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = _data.B.VariantId, countedQty = 1m } } });
        var foreignWriteoff = await Send(HttpMethod.Post, "/writeoffs", keeper, NewWriteoff("defect", _data.B.VariantId, 1m));
        var cashier = (await _client.LoginAsync(_data.A, "cashier")).AccessToken;
        var cashierWriteoff = await Send(HttpMethod.Post, "/writeoffs", cashier, NewWriteoff("defect", variant, 1m));
        await Send(HttpMethod.Post, $"/inventories/{inventory}/counts", keeper,
            new { items = new[] { new { variantId = variant, countedQty = 8m } } });
        await Send(HttpMethod.Post, $"/inventories/{inventory}/post", keeper);

        Assert.Equal(HttpStatusCode.BadRequest, foreignCount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignWriteoff.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cashierWriteoff.StatusCode);
        foreach (var sql in new[]
                 {
                     "UPDATE inventory_items SET counted_qty = 100 WHERE inventory_id = $1",
                     "DELETE FROM inventory_items WHERE inventory_id = $1",
                     "UPDATE inventories SET status = 'open', posted_at = NULL WHERE id = $1",
                     "DELETE FROM inventories WHERE id = $1",
                 })
        {
            Assert.True(await FailsAsAppAsync(sql, inventory), $"должно быть запрещено: {sql}");
        }
    }

    // -----------------------------------------------------------------------

    private sealed record Line(Guid id, Guid variantId, decimal qty);

    private sealed record WriteoffBody(Guid id, string reason, string? note, Line[] items);

    private static WriteoffBody NewWriteoff(string reason, Guid variant, decimal qty, string? note = null) =>
        new(Guid.NewGuid(), reason, note, [new Line(Guid.NewGuid(), variant, qty)]);

    private sealed record Kassa(string Token, Guid ShiftId)
    {
        private int _number;
        public int NextNumber() => Interlocked.Increment(ref _number);
    }

    private async Task<string> KeeperAsync() => (await _client.LoginAsync(_data.A, "keeper")).AccessToken;

    /// <summary>Новая касса филиала 1 с открытой сменой (как в SaleTests).</summary>
    private async Task<Kassa> NewKassaAsync()
    {
        var manager = await _client.LoginAsync(_data.A, "manager");
        var code = (await (await Send(HttpMethod.Post, "/devices/enrollment-codes", manager.AccessToken, new { })).JsonAsync()).GetProperty("code").GetString();
        var enrolled = await (await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса теста инвентаризации" })).JsonAsync();
        var device = new TestDevice(enrolled.GetProperty("deviceId").GetGuid(), enrolled.GetProperty("deviceSecret").GetString()!, _data.A.StoreId);
        var cashier = _data.A.Users["cashier"];
        var token = (await (await _client.PinAsync(device, cashier.Login, cashier.Pin!)).JsonAsync()).GetProperty("accessToken").GetString()!;
        var shiftId = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/shifts", token, new { id = shiftId, openingCash = 0m })).EnsureSuccessStatusCode();
        return new Kassa(token, shiftId);
    }

    private Task<HttpResponseMessage> SellAsync(Kassa kassa, Guid variant, int qty) =>
        Send(HttpMethod.Post, "/sales", kassa.Token, new SaleRequest(Guid.NewGuid(), kassa.ShiftId, kassa.NextNumber(), DateTimeOffset.UtcNow,
            [new SaleItem(Guid.NewGuid(), variant, qty, 200m, 0m, null, null)], [new Payment(Guid.NewGuid(), "cash", 200m * qty)]));

    private async Task<(Guid Product, Guid Variant)> NewGoodsAsync(string keeper)
    {
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/products", keeper, new
        {
            id = product, name = $"Склад {_data.Suffix} {product:N}",
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

    private async Task<decimal> StockAsync(string token, Guid product, Guid variant)
    {
        var card = await (await Send(HttpMethod.Get, $"/products/{product}", token)).JsonAsync();
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

    /// <summary>Количества движений пары с данной причиной, по возрастанию.</summary>
    private static async Task<List<decimal>> MovementsAsync(Guid variant, string reason)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT qty FROM stock_movements WHERE variant_id = $1 AND reason = $2 ORDER BY qty DESC", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = variant });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        var result = new List<decimal>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetDecimal(0));
        }

        return result;
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

    private async Task<bool> FailsAsAppAsync(string sql, Guid id)
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
            command.Parameters.Add(new NpgsqlParameter { Value = id });
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
