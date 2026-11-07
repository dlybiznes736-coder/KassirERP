using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KassirERP.Api.Tests.Infrastructure;

namespace KassirERP.Api.Tests;

/// <summary>Регистрация касс по коду менеджера и вход по PIN только с секретом кассы.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeviceTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    // --- Регистрация -------------------------------------------------------

    [Fact]
    public async Task Manager_code_enrolls_a_device_and_a_cashier_logs_in_on_it()
    {
        var device = await EnrollAsync(_data.A, "Касса у окна");
        var cashier = _data.A.Users["cashier"];

        var login = await _client.PinAsync(device, cashier.Login, cashier.Pin!);
        var token = (await login.JsonAsync()).GetProperty("accessToken").GetString()!;
        var me = await (await _client.SendAsync(ApiClient.Get("/auth/me", token))).JsonAsync();
        var manager = await _client.LoginAsync(_data.A, "manager");
        var list = await (await _client.SendAsync(ApiClient.Get("/devices", manager.AccessToken))).JsonAsync();

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(device.Id, me.GetProperty("deviceId").GetGuid());
        Assert.Equal(_data.A.StoreId, me.GetProperty("storeId").GetGuid());
        Assert.Contains(list.EnumerateArray(), d => d.GetProperty("id").GetGuid() == device.Id);
    }

    [Fact]
    public async Task Enrollment_code_works_only_once()
    {
        var code = await NewCodeAsync(_data.A);

        var first = await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса 1" });
        var second = await _client.PostAsJsonAsync("/devices/enroll", new { code, name = "Касса 2" });
        var wrong = await _client.PostAsJsonAsync("/devices/enroll", new { code = "00000000", name = "Касса 3" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Theory]
    [InlineData("cashier")]   // кассир
    [InlineData("manager")]   // менеджер, но вошёл по PIN с кассы
    public async Task Codes_are_issued_only_by_a_manager_logged_in_with_password(string userKey)
    {
        var user = _data.A.Users[userKey];
        var pinLogin = await _client.PinAsync(_data.A.DeviceFor(user), user.Login, user.Pin!);
        var token = (await pinLogin.JsonAsync()).GetProperty("accessToken").GetString()!;

        var response = await _client.SendAsync(ApiClient.PostJson("/devices/enrollment-codes", token, "{}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- PIN только с секретом кассы ------------------------------------------

    [Fact]
    public async Task Pin_without_device_secret_is_rejected()
    {
        var cashier = _data.A.Users["cashier"];

        var noDevice = await _client.PinAsync(null, cashier.Login, cashier.Pin!);
        var wrongSecret = await _client.PinAsync(_data.A.Devices["kassa1"], cashier.Login, cashier.Pin!, secret: "not-the-secret");
        var unknownDevice = await _client.PinAsync(new TestDevice(Guid.NewGuid(), "x", _data.A.StoreId), cashier.Login, cashier.Pin!);
        // Старый контракт: код фирмы в теле, без кассы.
        var oldContract = await _client.PostAsJsonAsync("/auth/pin", new { tenantCode = _data.A.Code, login = cashier.Login, pin = cashier.Pin });

        Assert.Equal(HttpStatusCode.Unauthorized, noDevice.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownDevice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oldContract.StatusCode);
    }

    [Fact]
    public async Task Device_of_another_tenant_never_logs_into_tenant_a()
    {
        var onlyInA = _data.A.Users["own"];          // логина «cashier-a» в фирме B нет
        var sameLogin = _data.A.Users["cashier"];    // логин «cashier» есть и в B
        var deviceB = _data.B.Devices["kassa1"];

        var foreign = await _client.PinAsync(deviceB, onlyInA.Login, onlyInA.Pin!);
        var sameName = await _client.PinAsync(deviceB, sameLogin.Login, sameLogin.Pin!);

        // Касса B определяет фирму: кассир A на ней либо не найден, либо это кассир B.
        Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
        Assert.Equal(_data.B.Id, (await sameName.JsonAsync()).GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task Cashier_of_store_1_cannot_log_in_on_a_device_of_store_2()
    {
        var cashier = _data.A.Users["cashier"];

        var response = await _client.PinAsync(_data.A.Devices["kassa2"], cashier.Login, cashier.Pin!);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_device_cannot_log_in_and_its_tokens_stop_working_at_once()
    {
        var device = await EnrollAsync(_data.A, "Касса на отключение");
        var cashier = _data.A.Users["cashier"];
        var token = (await (await _client.PinAsync(device, cashier.Login, cashier.Pin!)).JsonAsync()).GetProperty("accessToken").GetString()!;
        var manager = await _client.LoginAsync(_data.A, "manager");

        var revoke = await _client.SendAsync(ApiClient.PostJson($"/devices/{device.Id}/revoke", manager.AccessToken, "{}"));
        var oldToken = await _client.SendAsync(ApiClient.Get("/auth/me", token));
        var newLogin = await _client.PinAsync(device, cashier.Login, cashier.Pin!);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, newLogin.StatusCode);
    }

    // -----------------------------------------------------------------------

    private async Task<string> NewCodeAsync(TestTenant tenant)
    {
        var manager = await _client.LoginAsync(tenant, "manager");
        var response = await _client.SendAsync(ApiClient.PostJson("/devices/enrollment-codes", manager.AccessToken, "{}"));
        response.EnsureSuccessStatusCode();
        var body = await response.JsonAsync();
        Assert.Equal(tenant.StoreId, body.GetProperty("storeId").GetGuid());
        return body.GetProperty("code").GetString()!;
    }

    private async Task<TestDevice> EnrollAsync(TestTenant tenant, string name)
    {
        var code = await NewCodeAsync(tenant);
        var response = await _client.PostAsJsonAsync("/devices/enroll", new { code, name });
        response.EnsureSuccessStatusCode();
        var body = await response.JsonAsync();
        return new TestDevice(body.GetProperty("deviceId").GetGuid(), body.GetProperty("deviceSecret").GetString()!, body.GetProperty("storeId").GetGuid());
    }
}
