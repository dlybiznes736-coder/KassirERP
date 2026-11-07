using System.Net;
using System.Net.Http.Json;
using KassirERP.Api.Security;
using KassirERP.Api.Tests.Infrastructure;

namespace KassirERP.Api.Tests;

/// <summary>
/// Боевые лимиты выдерживают начало смены магазина на 50 касс за одним NAT.
/// В TestServer у всех запросов один адрес — ровно как за NAT.
/// </summary>
public sealed class ProductionAuthRateLimitTests(ProductionRateLimitFactory factory) : IClassFixture<ProductionRateLimitFactory>
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public void Defaults_cover_one_device_and_the_shift_start()
    {
        var pin = new PinRateLimitOptions();

        // Одна касса: вход + до 4 ошибок до блокировки учётки + пересменка.
        Assert.True(pin.TokenLimit >= AuthServiceAttempts * 2);
        Assert.Equal(10, pin.TokensPerMinute);
        Assert.Equal(200, new AuthRateLimitOptions().TokenLimit);
    }

    private const int AuthServiceAttempts = KassirERP.Application.Auth.AuthService.MaxPinAttempts;

    [Fact]
    public async Task Fifty_cashiers_behind_one_nat_start_a_shift_without_429()
    {
        var cashiers = Enumerable.Range(1, ProductionRateLimitFactory.Cashiers)
            .Select(i => (User: _data.A.Users[$"cashier-{i:D2}"], Device: _data.A.Devices[$"kassa-{i:D2}"])).ToList();

        // Все 50 одновременно, каждый со своей кассы: дважды ошибся в PIN, потом вошёл.
        // 150 запросов с одного IP — лимит PIN считается по кассе, поэтому 429 нет.
        var wrong = await Task.WhenAll(cashiers.SelectMany(c => new[]
        {
            _client.PinAsync(c.Device, c.User.Login, "0000"),
            _client.PinAsync(c.Device, c.User.Login, "1111"),
        }));
        var right = await Task.WhenAll(cashiers.Select(c => _client.PinAsync(c.Device, c.User.Login, c.User.Pin!)));

        Assert.All(wrong, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.All(right, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

/// <summary>Сверх лимита — 429 с Retry-After; лимит PIN считается по кассе, а не по IP.</summary>
public sealed class TinyAuthRateLimitTests(TinyRateLimitFactory factory) : IClassFixture<TinyRateLimitFactory>
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Password_logins_over_the_ip_limit_get_429_with_retry_after()
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < TinyRateLimitFactory.Limit; i++)
        {
            codes.Add((await _client.PostAsJsonAsync("/auth/login", new { tenantCode = _data.A.Code, login = "nobody", password = "x" })).StatusCode);
        }

        var over = await _client.PostAsJsonAsync("/auth/login", new { tenantCode = _data.A.Code, login = "nobody", password = "x" });

        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.NotNull(over.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Pin_limit_is_counted_per_device_not_per_ip()
    {
        // Касса 1 исчерпала свой лимит. Касса 2 с того же IP — входит.
        var kassa1 = _data.B.Devices["kassa1"];
        var kassa2 = _data.B.Devices["kassa2"];
        var user = _data.B.Users["multi"];   // доступ к филиалам 1 и 2

        var spent = new List<HttpStatusCode>();
        for (var i = 0; i < TinyRateLimitFactory.PinLimit; i++)
        {
            spent.Add((await _client.PinAsync(kassa1, "nobody", "0000")).StatusCode);
        }

        var kassa1Over = await _client.PinAsync(kassa1, user.Login, user.Pin!);
        var kassa2Fine = await _client.PinAsync(kassa2, user.Login, user.Pin!);

        Assert.All(spent, c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.Equal(HttpStatusCode.TooManyRequests, kassa1Over.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kassa2Fine.StatusCode);
    }
}
