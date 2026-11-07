using System.Net;
using System.Net.Http.Json;
using KassirERP.Api.Security;
using KassirERP.Api.Tests.Infrastructure;

namespace KassirERP.Api.Tests;

/// <summary>
/// Боевой лимит /auth/* выдерживает начало смены магазина на 50 касс за одним
/// NAT. В TestServer у всех запросов один адрес — ровно как за NAT.
/// </summary>
public sealed class ProductionAuthRateLimitTests(ProductionRateLimitFactory factory) : IClassFixture<ProductionRateLimitFactory>
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public void Defaults_cover_the_worst_shift_start_burst()
    {
        var defaults = new AuthRateLimitOptions();

        // 50 касс × (вход + до 3 ошибок ввода PIN до блокировки на 5-й) = 200.
        Assert.True(defaults.TokenLimit >= ProductionRateLimitFactory.Cashiers * 4);
        Assert.Equal(60, defaults.TokensPerMinute);
    }

    [Fact]
    public async Task Fifty_cashiers_behind_one_nat_start_a_shift_without_429()
    {
        var cashiers = Enumerable.Range(1, ProductionRateLimitFactory.Cashiers)
            .Select(i => _data.A.Users[$"cashier-{i:D2}"]).ToList();

        Task<HttpResponseMessage> Pin(TestUser user, string pin) =>
            _client.PostAsJsonAsync("/auth/pin", new { tenantCode = _data.A.Code, login = user.Login, pin });

        // Все 50 одновременно: каждый дважды ошибся в PIN, потом вошёл. 150 запросов разом.
        var wrong = await Task.WhenAll(cashiers.SelectMany(c => new[] { Pin(c, "0000"), Pin(c, "1111") }));
        var right = await Task.WhenAll(cashiers.Select(c => Pin(c, c.Pin!)));

        Assert.All(wrong, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.All(right, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

/// <summary>Сверх лимита — 429 с Retry-After, и лимит общий для пароля и PIN.</summary>
public sealed class TinyAuthRateLimitTests(TinyRateLimitFactory factory) : IClassFixture<TinyRateLimitFactory>
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Requests_over_the_limit_get_429_with_retry_after()
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < TinyRateLimitFactory.Limit; i++)
        {
            var endpoint = i % 2 == 0 ? "/auth/pin" : "/auth/login";
            object body = i % 2 == 0
                ? new { tenantCode = _data.A.Code, login = "nobody", pin = "0000" }
                : new { tenantCode = _data.A.Code, login = "nobody", password = "x" };
            codes.Add((await _client.PostAsJsonAsync(endpoint, body)).StatusCode);
        }

        var over = await _client.PostAsJsonAsync("/auth/login", new { tenantCode = _data.A.Code, login = "nobody", password = "x" });

        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.NotNull(over.Headers.RetryAfter?.Delta);
    }
}
