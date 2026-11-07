using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>
/// Перемещение: отправка → в пути → приём. Отправляет кладовщик филиала 1,
/// принимает владелец, вошедший в филиал 2. Каждый тест — свой товар с
/// приходом 10 шт в филиал 1.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransferTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Sending_decreases_sender_stock_and_receiving_increases_receiver_stock()
    {
        var (sender, receiver) = await TokensAsync();
        var (product, variant) = await NewGoodsAsync(sender);
        var transfer = NewTransfer(variant, 4m);

        var created = await Send(HttpMethod.Post, "/transfers", sender, transfer);
        var afterCreate = (await StockAsync(sender, product, variant), await StockAsync(receiver, product, variant));
        var sent = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", sender);
        var afterSend = (await StockAsync(sender, product, variant), await StockAsync(receiver, product, variant));
        var incoming = await (await Send(HttpMethod.Get, "/transfers/incoming", receiver)).JsonAsync();
        var received = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver, new { });
        var afterReceive = (await StockAsync(sender, product, variant), await StockAsync(receiver, product, variant));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal((10m, 0m), afterCreate);                     // черновик остатки не трогает
        Assert.Equal("in_transit", (await sent.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal((6m, 0m), afterSend);                        // в пути: у отправителя уже нет, у получателя ещё нет
        Assert.Contains(incoming.EnumerateArray(), t => t.GetProperty("id").GetGuid() == transfer.id);
        var body = await received.JsonAsync();
        Assert.Equal("received", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("hasDiscrepancy").GetBoolean());
        Assert.Equal((6m, 4m), afterReceive);
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Resending_does_not_create_duplicates()
    {
        var (sender, receiver) = await TokensAsync();
        var (product, variant) = await NewGoodsAsync(sender);
        var transfer = NewTransfer(variant, 3m);

        var codes = new List<HttpStatusCode>
        {
            (await Send(HttpMethod.Post, "/transfers", sender, transfer)).StatusCode,
            (await Send(HttpMethod.Post, "/transfers", sender, transfer)).StatusCode,
            (await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", sender)).StatusCode,
            (await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", sender)).StatusCode,
            (await Send(HttpMethod.Post, "/transfers", sender, transfer)).StatusCode,
            (await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver, new { })).StatusCode,
            (await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver, new { })).StatusCode,
        };
        var changedAfterReceive = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver,
            new { items = new[] { new { id = transfer.items[0].id, qtyReceived = 1m } } });

        Assert.Equal([HttpStatusCode.Created, .. Enumerable.Repeat(HttpStatusCode.OK, 6)], codes);
        Assert.Equal(HttpStatusCode.Conflict, changedAfterReceive.StatusCode);   // принятое не меняется
        Assert.Equal((1L, 1L, 1L, 1L), await CountsAsync(transfer.id));         // документ, строка, отправка, приём
        Assert.Equal((7m, 3m), (await StockAsync(sender, product, variant), await StockAsync(receiver, product, variant)));
    }

    [Fact]
    public async Task Receiving_less_than_sent_records_the_shortage()
    {
        var (sender, receiver) = await TokensAsync();
        var (product, variant) = await NewGoodsAsync(sender);
        var transfer = NewTransfer(variant, 10m);
        await Send(HttpMethod.Post, "/transfers", sender, transfer);
        await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", sender);

        var received = await (await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver,
            new { items = new[] { new { id = transfer.items[0].id, qtyReceived = 7m } } })).JsonAsync();
        var line = received.GetProperty("items")[0];

        Assert.True(received.GetProperty("hasDiscrepancy").GetBoolean());
        Assert.Equal(10m, line.GetProperty("qtySent").GetDecimal());
        Assert.Equal(7m, line.GetProperty("qtyReceived").GetDecimal());
        Assert.Equal(3m, line.GetProperty("shortage").GetDecimal());
        Assert.Equal((0m, 7m), (await StockAsync(sender, product, variant), await StockAsync(receiver, product, variant)));
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Wrong_store_wrong_stage_and_over_receipt_are_refused()
    {
        var (sender, receiver) = await TokensAsync();
        var (_, variant) = await NewGoodsAsync(sender);
        var transfer = NewTransfer(variant, 2m);
        await Send(HttpMethod.Post, "/transfers", sender, transfer);

        var receiveDraft = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver, new { });
        var sendByReceiver = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", receiver);
        await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", sender);
        var receiveBySender = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", sender, new { });
        var overReceipt = await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver,
            new { items = new[] { new { id = transfer.items[0].id, qtyReceived = 3m } } });

        Assert.Equal(HttpStatusCode.Conflict, receiveDraft.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sendByReceiver.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, receiveBySender.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overReceipt.StatusCode);
    }

    [Fact]
    public async Task Transfers_stay_inside_the_tenant_and_received_ones_do_not_change()
    {
        var (sender, receiver) = await TokensAsync();
        var (_, variant) = await NewGoodsAsync(sender);

        var toForeignStore = await Send(HttpMethod.Post, "/transfers", sender, NewTransfer(variant, 1m, _data.B.StoreId));
        var foreignVariant = await Send(HttpMethod.Post, "/transfers", sender, NewTransfer(_data.B.VariantId, 1m));
        var transfer = NewTransfer(variant, 2m);
        await Send(HttpMethod.Post, "/transfers", sender, transfer);
        await Send(HttpMethod.Post, $"/transfers/{transfer.id}/send", sender);
        await Send(HttpMethod.Post, $"/transfers/{transfer.id}/receive", receiver, new { });

        Assert.Equal(HttpStatusCode.BadRequest, toForeignStore.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignVariant.StatusCode);
        foreach (var sql in new[]
                 {
                     "UPDATE transfer_items SET qty_received = 0 WHERE transfer_id = $1",
                     "UPDATE transfers SET status = 'in_transit' WHERE id = $1",
                     "DELETE FROM transfers WHERE id = $1",
                 })
        {
            Assert.True(await FailsAsAppAsync(sql, transfer.id), $"должно быть запрещено: {sql}");
        }
    }

    // -----------------------------------------------------------------------

    private sealed record Line(Guid id, Guid variantId, decimal qty);

    private sealed record TransferBody(Guid id, Guid toStoreId, Line[] items);

    private TransferBody NewTransfer(Guid variant, decimal qty, Guid? to = null) =>
        new(Guid.NewGuid(), to ?? _data.A.Store2Id, [new Line(Guid.NewGuid(), variant, qty)]);

    private async Task<(string Sender, string Receiver)> TokensAsync() =>
        ((await _client.LoginAsync(_data.A, "keeper")).AccessToken,
         (await _client.LoginAsync(_data.A, "owner", _data.A.Store2Id)).AccessToken);

    private async Task<(Guid Product, Guid Variant)> NewGoodsAsync(string keeper)
    {
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        (await Send(HttpMethod.Post, "/products", keeper, new
        {
            id = product, name = $"Перемещение {_data.Suffix} {product:N}",
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

    private static async Task<(long Docs, long Lines, long Out, long In)> CountsAsync(Guid transferId)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM transfers WHERE id = $1), (SELECT count(*) FROM transfer_items WHERE transfer_id = $1),
                   (SELECT count(*) FROM stock_movements WHERE doc_id = $1 AND reason = 'transfer_out'),
                   (SELECT count(*) FROM stock_movements WHERE doc_id = $1 AND reason = 'transfer_in')
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = transferId });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
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
