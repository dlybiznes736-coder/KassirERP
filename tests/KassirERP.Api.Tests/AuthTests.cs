using System.Net;
using System.Net.Http.Json;
using KassirERP.Api.Tests.Infrastructure;
using KassirERP.Application.Auth;
using KassirERP.Infrastructure.Auth;
using Npgsql;

namespace KassirERP.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Password_login_puts_tenant_store_and_role_into_token()
    {
        var keeper = _data.A.Users["keeper"];

        var login = await _client.LoginAsync(_data.A, "keeper");
        var payload = ApiClient.Base64UrlDecode(login.AccessToken.Split('.')[1]);

        Assert.Equal(_data.A.Id, login.TenantId);
        Assert.Equal(_data.A.StoreId, login.StoreId);
        Assert.Equal("storekeeper", login.Role);
        Assert.Contains($"\"{JwtClaims.TenantId}\":\"{_data.A.Id}\"", payload);
        Assert.Contains($"\"{JwtClaims.StoreId}\":\"{_data.A.StoreId}\"", payload);
        Assert.Contains($"\"sub\":\"{keeper.Id}\"", payload);
    }

    [Fact]
    public async Task Owner_without_store_can_choose_own_store_but_not_other_tenant_store()
    {
        var own = await _client.LoginAsync(_data.A, "owner", storeId: _data.A.StoreId);
        var foreign = await _client.PostAsJsonAsync("/auth/login", new
        {
            tenantCode = _data.A.Code, login = "owner", password = _data.A.Users["owner"].Password, storeId = _data.B.StoreId,
        });

        Assert.Equal(_data.A.StoreId, own.StoreId);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    [Theory]
    [InlineData("keeper", "wrong-password")]   // неверный пароль
    [InlineData("nobody", "whatever-123")]     // нет такого логина
    public async Task Wrong_credentials_give_the_same_401(string login, string password)
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new { tenantCode = _data.A.Code, login, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Credentials_of_tenant_a_do_not_work_under_tenant_b_code()
    {
        var keeper = _data.A.Users["keeper"];

        var response = await _client.PostAsJsonAsync("/auth/login", new { tenantCode = _data.B.Code, login = keeper.Login, password = keeper.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Доступ кассира к филиалам — явный список (user_stores). Без филиалов вход
    // запрещён: «без привязки» больше не значит «все филиалы».
    [Fact]
    public async Task Cashier_without_stores_cannot_log_in()
    {
        var user = _data.A.Users["nostore"];

        var kassa1 = await _client.PinAsync(_data.A.Devices["kassa1"], user.Login, user.Pin!);
        var kassa2 = await _client.PinAsync(_data.A.Devices["kassa2"], user.Login, user.Pin!);

        Assert.Equal(HttpStatusCode.Forbidden, kassa1.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kassa2.StatusCode);
    }

    [Fact]
    public async Task Cashier_with_stores_1_and_2_logs_in_only_there_not_on_store_3()
    {
        var user = _data.A.Users["multi"];

        var store1 = await _client.PinAsync(_data.A.Devices["kassa1"], user.Login, user.Pin!);
        var store2 = await _client.PinAsync(_data.A.Devices["kassa2"], user.Login, user.Pin!);
        var store3 = await _client.PinAsync(_data.A.Devices["kassa3"], user.Login, user.Pin!);

        Assert.Equal(HttpStatusCode.OK, store1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, store2.StatusCode);
        Assert.Equal(_data.A.Store2Id, (await store2.JsonAsync()).GetProperty("storeId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, store3.StatusCode);
    }

    [Fact]
    public async Task Pin_is_locked_after_five_wrong_attempts_even_for_the_right_pin()
    {
        var user = _data.A.Users["locked"];
        var device = _data.A.DeviceFor(user);
        async Task<HttpResponseMessage> Try(string pin) => await _client.PinAsync(device, user.Login, pin);

        for (var i = 0; i < AuthService.MaxPinAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Try("0000")).StatusCode);
        }

        var right = await Try(user.Pin!);

        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.True(right.Headers.RetryAfter?.Delta > TimeSpan.FromMinutes(14), "Retry-After ≈ 15 минут");
    }

    [Fact]
    public async Task Parallel_pin_guessing_cannot_exceed_attempt_limit()
    {
        // 30 одновременных попыток: счётчик резервируется до проверки хеша,
        // поэтому проверено будет не больше MaxPinAttempts вариантов.
        var user = _data.B.Users["locked"];
        var device = _data.B.DeviceFor(user);
        var tasks = Enumerable.Range(0, 30).Select(i => _client.PinAsync(device, user.Login, (1000 + i).ToString()));

        var codes = (await Task.WhenAll(tasks)).Select(r => r.StatusCode).ToList();

        Assert.True(codes.Count(c => c == HttpStatusCode.Unauthorized) <= AuthService.MaxPinAttempts,
            $"проверено {codes.Count(c => c == HttpStatusCode.Unauthorized)} PIN, лимит {AuthService.MaxPinAttempts}");
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task Passwords_and_pins_are_stored_as_salted_hashes_only()
    {
        await using var connection = new NpgsqlConnection(KassirApiFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT login, password_hash, pin_hash FROM users WHERE tenant_id = ANY($1)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = new[] { _data.A.Id, _data.B.Id } });
        await using var reader = await command.ExecuteReaderAsync();

        var hashes = new List<string>();
        while (await reader.ReadAsync())
        {
            foreach (var ordinal in new[] { 1, 2 })
            {
                if (reader.IsDBNull(ordinal))
                {
                    continue;
                }

                var hash = reader.GetString(ordinal);
                Assert.StartsWith("pbkdf2-sha512$210000$", hash);
                hashes.Add(hash);
            }
        }

        var secrets = new[] { _data.A, _data.B }.SelectMany(t => t.Users.Values).SelectMany(u => new[] { u.Password, u.Pin }).OfType<string>();
        Assert.All(secrets, secret => Assert.DoesNotContain(hashes, h => h.Contains(secret, StringComparison.Ordinal)));

        // Одинаковые PIN («4821» у кассиров A и B) — разные хеши: соль своя у каждого.
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }
}
