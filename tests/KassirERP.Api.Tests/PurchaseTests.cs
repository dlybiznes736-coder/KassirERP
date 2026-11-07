using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;
using Npgsql;

namespace KassirERP.Api.Tests;

/// <summary>Приём товара (шаг 4): накладная, проведение, движение склада, срок годности в строке.</summary>
[Collection(ApiCollection.Name)]
public sealed class PurchaseTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Accepting_a_purchase_adds_stock_and_writes_expiry_into_the_movement()
    {
        var keeper = await KeeperAsync();
        var (productId, variantId) = await NewProductAsync(keeper);
        var purchaseId = Guid.NewGuid();
        var expires = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        var created = await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = purchaseId, number = "ПН-1",
            items = new[] { new { id = Guid.NewGuid(), variantId, qty = 12.5m, costPrice = 100.00m, expiresOn = expires } },
        });
        var stockBefore = await StockAsync(keeper, productId, variantId);
        var accepted = await Send(HttpMethod.Post, $"/purchases/{purchaseId}/accept", keeper, null);
        var stockAfter = await StockAsync(keeper, productId, variantId);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("draft", (await created.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("accepted", (await accepted.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(0m, stockBefore);   // черновик остаток не трогает
        Assert.Equal(12.5m, stockAfter);
        Assert.Equal(expires, await MovementExpiryAsync(purchaseId));
        Assert.True(await StockMatchesJournalAsync(), "stock = SUM(stock_movements)");
    }

    [Fact]
    public async Task Accepted_purchase_cannot_be_changed_or_deleted()
    {
        var keeper = await KeeperAsync();
        var (productId, variantId) = await NewProductAsync(keeper);
        var purchaseId = Guid.NewGuid();
        await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = purchaseId, items = new[] { new { id = Guid.NewGuid(), variantId, qty = 5m, costPrice = 10m } },
        });
        await Send(HttpMethod.Post, $"/purchases/{purchaseId}/accept", keeper, null);

        var edit = await Send(HttpMethod.Put, $"/purchases/{purchaseId}/items", keeper, new
        {
            items = new[] { new { id = Guid.NewGuid(), variantId, qty = 500m, costPrice = 10m } },
        });
        var delete = await Send(HttpMethod.Delete, $"/purchases/{purchaseId}", keeper, null);
        var after = await (await Send(HttpMethod.Get, $"/purchases/{purchaseId}", keeper, null)).JsonAsync();

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(5m, after.GetProperty("items")[0].GetProperty("qty").GetDecimal());
        Assert.Equal(5m, await StockAsync(keeper, productId, variantId));
    }

    [Fact]
    public async Task Draft_can_be_changed_and_deleted()
    {
        var keeper = await KeeperAsync();
        var (_, variantId) = await NewProductAsync(keeper);
        var purchaseId = Guid.NewGuid();
        await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = purchaseId, items = new[] { new { id = Guid.NewGuid(), variantId, qty = 1m, costPrice = 10m } },
        });

        var edit = await Send(HttpMethod.Put, $"/purchases/{purchaseId}/items", keeper, new
        {
            items = new[] { new { id = Guid.NewGuid(), variantId, qty = 3m, costPrice = 12.50m } },
        });
        var delete = await Send(HttpMethod.Delete, $"/purchases/{purchaseId}", keeper, null);
        var gone = await Send(HttpMethod.Get, $"/purchases/{purchaseId}", keeper, null);

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(37.50m, (await edit.JsonAsync()).GetProperty("total").GetDecimal());
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Resending_the_same_purchase_and_accept_does_not_create_duplicates()
    {
        var keeper = await KeeperAsync();
        var (productId, variantId) = await NewProductAsync(keeper);
        var purchaseId = Guid.NewGuid();
        var body = new { id = purchaseId, number = "ПН-2", items = new[] { new { id = Guid.NewGuid(), variantId, qty = 4m, costPrice = 50m } } };

        var first = await Send(HttpMethod.Post, "/purchases", keeper, body);
        var again = await Send(HttpMethod.Post, "/purchases", keeper, body);
        var accept1 = await Send(HttpMethod.Post, $"/purchases/{purchaseId}/accept", keeper, null);
        var accept2 = await Send(HttpMethod.Post, $"/purchases/{purchaseId}/accept", keeper, null);
        var againAfterAccept = await Send(HttpMethod.Post, "/purchases", keeper, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);              // повтор — тот же документ
        Assert.Equal(HttpStatusCode.OK, accept1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accept2.StatusCode);            // повторное проведение — без второго движения
        Assert.Equal(HttpStatusCode.OK, againAfterAccept.StatusCode);
        Assert.Equal((1L, 1L, 1L), await CountsAsync(purchaseId));      // накладная, строка, движение — по одной
        Assert.Equal(4m, await StockAsync(keeper, productId, variantId));
    }

    [Fact]
    public async Task Same_id_with_different_content_is_a_conflict()
    {
        var keeper = await KeeperAsync();
        var (_, variantId) = await NewProductAsync(keeper);
        var purchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await Send(HttpMethod.Post, "/purchases", keeper, new { id = purchaseId, items = new[] { new { id = itemId, variantId, qty = 1m, costPrice = 10m } } });
        var changed = await Send(HttpMethod.Post, "/purchases", keeper, new { id = purchaseId, items = new[] { new { id = itemId, variantId, qty = 2m, costPrice = 10m } } });

        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task Expiry_is_required_for_tracked_goods_and_must_not_be_in_the_past()
    {
        var keeper = await KeeperAsync();
        var variantId = _data.A.ExpiryVariantId;   // молоко: tracks_expiry = true
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var missing = await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = Guid.NewGuid(), items = new[] { new { id = Guid.NewGuid(), variantId, qty = 10m, costPrice = 60m } },
        });
        var expired = await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = Guid.NewGuid(), items = new[] { new { id = Guid.NewGuid(), variantId, qty = 10m, costPrice = 60m, expiresOn = today.AddDays(-1) } },
        });
        var ok = await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = Guid.NewGuid(), items = new[] { new { id = Guid.NewGuid(), variantId, qty = 10m, costPrice = 60m, expiresOn = today.AddDays(7) } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Variant_of_another_tenant_cannot_be_received()
    {
        var keeper = await KeeperAsync();
        var purchaseId = Guid.NewGuid();

        var response = await Send(HttpMethod.Post, "/purchases", keeper, new
        {
            id = purchaseId, items = new[] { new { id = Guid.NewGuid(), variantId = _data.B.VariantId, qty = 1000m, costPrice = 1m } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0L, 0L, 0L), await CountsAsync(purchaseId));
    }

    [Fact]
    public async Task Cashier_cannot_receive_goods()
    {
        var cashier = await _client.LoginAsync(_data.A, "cashier");

        var response = await Send(HttpMethod.Post, "/purchases", cashier.AccessToken, new
        {
            id = Guid.NewGuid(), items = new[] { new { id = Guid.NewGuid(), variantId = _data.A.VariantId, qty = 1m, costPrice = 1m } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -----------------------------------------------------------------------

    private async Task<string> KeeperAsync() => (await _client.LoginAsync(_data.A, "keeper")).AccessToken;

    /// <summary>Свой товар на каждый тест: остаток 0, другие тесты его не трогают.</summary>
    private async Task<(Guid ProductId, Guid VariantId)> NewProductAsync(string token)
    {
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var response = await Send(HttpMethod.Post, "/products", token, new
        {
            id = productId, name = $"Приход {_data.Suffix} {productId:N}",
            variants = new[] { new { id = variantId, costPrice = 10m, salePrice = 20m } },
        });
        response.EnsureSuccessStatusCode();
        return (productId, variantId);
    }

    private async Task<decimal> StockAsync(string token, Guid productId, Guid variantId)
    {
        var product = await (await Send(HttpMethod.Get, $"/products/{productId}", token, null)).JsonAsync();
        return product.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("id").GetGuid() == variantId).GetProperty("stock").GetDecimal();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<(long Purchases, long Items, long Movements)> CountsAsync(Guid purchaseId)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM purchases WHERE id = $1),
                   (SELECT count(*) FROM purchase_items WHERE purchase_id = $1),
                   (SELECT count(*) FROM stock_movements WHERE doc_id = $1)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = purchaseId });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<DateOnly?> MovementExpiryAsync(Guid purchaseId)
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT expires_on FROM stock_movements WHERE doc_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = purchaseId });
        return await command.ExecuteScalarAsync() is DateOnly d ? d : null;
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
