using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KassirERP.Api.Tests.Infrastructure;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Поздние документы (партия C): чек или возврат пришёл синхронизацией, когда
/// его смена уже закрыта. Две кассы филиала 1: покупку пробили на кассе 2 без
/// связи, вернули на кассе 1.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LateDocumentTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Return_from_another_kassa_after_the_shift_closed_is_accepted_and_the_shift_is_recounted()
    {
        var kassa1 = await NewKassaAsync();
        var kassa2 = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync();
        var shift1 = Guid.NewGuid();
        var shift2 = Guid.NewGuid();
        var sale = Sale(shift2, 1, variant, 1);                    // касса 2, без связи
        var refund = Return(shift1, 1, sale, 1);                   // касса 1: 200 наличными покупателю

        // Касса 1: возврат ждёт свой чек, смена закрыта; кассир насчитал 1 000 − 200 = 800.
        var first = await PushAsync(kassa1, new
        {
            shifts = new[] { new { id = shift1, openingCash = 1000m } },
            sales = new[] { refund },
            closes = new[] { new { shiftId = shift1, closingCash = 800m } },
        });
        var closedBefore = await ShiftAsync(kassa1, shift1);

        // Касса 2 появилась на связи; касса 1 присылает возврат снова.
        await PushAsync(kassa2, new { shifts = new[] { new { id = shift2, openingCash = 0m } }, sales = new[] { sale } });
        var late = await PushAsync(kassa1, new { sales = new[] { refund } });
        var closedAfter = await ShiftAsync(kassa1, shift1);
        var stored = await (await Send(HttpMethod.Get, $"/sales/{refund.Id}", kassa1)).JsonAsync();
        var journal = await JournalAsync(shift1);
        var again = await PushAsync(kassa1, new { sales = new[] { refund } });

        Assert.Equal(["retry"], Statuses(first, "sales"));
        Assert.Equal(["accepted"], Statuses(first, "closes"));
        Assert.Equal(1000m, closedBefore.GetProperty("expectedCash").GetDecimal());
        Assert.False(closedBefore.GetProperty("changedAfterClose").GetBoolean());

        Assert.Equal(["accepted"], Statuses(late, "sales"));
        Assert.Contains("закрытую смену", late.GetProperty("sales")[0].GetProperty("note").GetString());
        Assert.Equal("closed", closedAfter.GetProperty("status").GetString());
        Assert.True(closedAfter.GetProperty("changedAfterClose").GetBoolean());          // смена помечена
        Assert.Equal(800m, closedAfter.GetProperty("expectedCash").GetDecimal());        // пересчитаны: 1 000 − 200
        Assert.Equal(800m, closedAfter.GetProperty("closingCash").GetDecimal());         // посчитанные кассиром не тронуты
        Assert.True(stored.GetProperty("afterClose").GetBoolean());
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("reviewReason").ValueKind);
        Assert.Equal([(refund.Id, "return", -200m, 1000m, 800m)], journal);              // журнал: документ, было, стало
        Assert.Equal(["duplicate"], Statuses(again, "sales"));
        Assert.Single(await JournalAsync(shift1));                                       // повтор журнал не множит
        Assert.Equal(10m, await StockAsync(product, variant));                           // 10 − 1 + 1
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Late_return_above_the_sold_quantity_is_accepted_and_flagged_for_review()
    {
        var kassa1 = await NewKassaAsync();
        var kassa2 = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync();
        var shift1 = Guid.NewGuid();
        var shift2 = Guid.NewGuid();
        var sale = Sale(shift2, 1, variant, 1);
        var refundA = Return(shift1, 1, sale, 1);                  // одну пару вернули дважды на кассе 1
        var refundB = Return(shift1, 2, sale, 1);

        await PushAsync(kassa1, new
        {
            shifts = new[] { new { id = shift1, openingCash = 500m } },
            sales = new[] { refundA, refundB },
            closes = new[] { new { shiftId = shift1, closingCash = 100m } },
        });
        await PushAsync(kassa2, new { shifts = new[] { new { id = shift2, openingCash = 0m } }, sales = new[] { sale } });
        var late = await PushAsync(kassa1, new { sales = new[] { refundA, refundB } });
        var flagged = await (await Send(HttpMethod.Get, $"/sales/{refundB.Id}", kassa1)).JsonAsync();
        var shift = await ShiftAsync(kassa1, shift1);

        Assert.Equal(["accepted", "accepted"], Statuses(late, "sales"));
        Assert.Contains("на разбор", late.GetProperty("sales")[1].GetProperty("note").GetString());
        Assert.Contains("больше проданного", flagged.GetProperty("reviewReason").GetString());
        Assert.Equal(100m, shift.GetProperty("expectedCash").GetDecimal());             // 500 − 200 − 200
        Assert.Equal(2, (await JournalAsync(shift1)).Count);
        Assert.Equal(11m, await StockAsync(product, variant));                          // 10 − 1 + 2: факт, видно в разборе
    }

    [Fact]
    public async Task Late_sale_is_accepted_but_online_sale_and_direct_edits_of_a_closed_shift_are_refused()
    {
        var kassa = await NewKassaAsync();
        var (product, variant) = await NewGoodsAsync();
        var shift = Guid.NewGuid();
        var lateSale = Sale(shift, 2, variant, 2);
        await PushAsync(kassa, new
        {
            shifts = new[] { new { id = shift, openingCash = 0m } },
            sales = new[] { Sale(shift, 1, variant, 1) },
            closes = new[] { new { shiftId = shift, closingCash = 200m } },
        });

        var online = await Send(HttpMethod.Post, "/sales", kassa, Sale(shift, 3, variant, 1));
        var late = await PushAsync(kassa, new { sales = new[] { lateSale } });
        var closed = await ShiftAsync(kassa, shift);

        Assert.Equal(HttpStatusCode.Conflict, online.StatusCode);                     // онлайн в закрытую смену — как раньше
        Assert.Equal(["accepted"], Statuses(late, "sales"));
        Assert.Equal(600m, closed.GetProperty("expectedCash").GetDecimal());           // 200 + 400
        Assert.Equal(200m, closed.GetProperty("closingCash").GetDecimal());
        Assert.Equal(7m, await StockAsync(product, variant));
        foreach (var sql in new[]
                 {
                     "UPDATE cash_shifts SET expected_cash = 600, changed_after_close = true WHERE id = $1",
                     "UPDATE cash_shifts SET closing_cash = 600 WHERE id = $1",
                     "INSERT INTO shift_changes (id, tenant_id, shift_id, sale_id, sale_type, cash_delta, expected_after) " +
                     "SELECT gen_random_uuid(), tenant_id, id, (SELECT id FROM sales WHERE shift_id = $1 LIMIT 1), 'sale', 0, 0 FROM cash_shifts WHERE id = $1",
                 })
        {
            Assert.True(await FailsAsAppAsync(sql, shift), $"должно быть запрещено: {sql}");
        }
    }

    // -----------------------------------------------------------------------

    private static SaleItem Line(Guid variant, decimal qty) => new(Guid.NewGuid(), variant, qty, 200m, 0m, null, null);

    private static Payment Pay(decimal amount) => new(Guid.NewGuid(), "cash", amount);

    private static SaleRequest Sale(Guid shift, int number, Guid variant, decimal qty) =>
        new(Guid.NewGuid(), shift, number, DateTimeOffset.UtcNow, [Line(variant, qty)], [Pay(200m * qty)]);

    private static SaleRequest Return(Guid shift, int number, SaleRequest original, decimal qty) =>
        new(Guid.NewGuid(), shift, number, DateTimeOffset.UtcNow,
            [new SaleItem(Guid.NewGuid(), null, qty, null, 0m, null, original.Items[0].Id)], [Pay(200m * qty)], "return", original.Id);

    private static List<string?> Statuses(JsonElement result, string section) =>
        result.GetProperty(section).EnumerateArray().Select(s => s.GetProperty("status").GetString()).ToList();

    private async Task<JsonElement> PushAsync(string kassa, object batch)
    {
        var response = await Send(HttpMethod.Post, "/sync/push", kassa, batch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<JsonElement> ShiftAsync(string kassa, Guid shift) =>
        await (await Send(HttpMethod.Get, $"/shifts/{shift}", kassa)).JsonAsync();

    private async Task<string> NewKassaAsync()
    {
        var manager = await _client.LoginAsync(_data.A, "manager");
        var code = (await (await Send(HttpMethod.Post, "/devices/enrollment-codes", manager.AccessToken, new { })).JsonAsync()).GetProperty("code").GetString();
        var enrolled = await (await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса поздних документов" })).JsonAsync();
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
            id = product, name = $"Поздний {_data.Suffix} {product:N}",
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

    private static async Task<List<(Guid Sale, string Type, decimal Delta, decimal Before, decimal After)>> JournalAsync(Guid shift)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sale_id, sale_type, cash_delta, expected_before, expected_after FROM shift_changes WHERE shift_id = $1 ORDER BY changed_at, id", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = shift });
        var rows = new List<(Guid, string, decimal, decimal, decimal)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }

        return rows;
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
