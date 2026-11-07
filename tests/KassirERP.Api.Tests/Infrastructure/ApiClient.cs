using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KassirERP.Api.Endpoints;

namespace KassirERP.Api.Tests.Infrastructure;

public static class ApiClient
{
    /// <summary>Вход: по паролю, если он есть у пользователя, иначе по PIN с кассы его филиала.</summary>
    public static async Task<LoginResponse> LoginAsync(this HttpClient client, TestTenant tenant, string userKey, Guid? storeId = null)
    {
        var user = tenant.Users[userKey];
        var response = user.Password is not null
            ? await client.PostAsJsonAsync("/auth/login", new { tenantCode = tenant.Code, login = user.Login, password = user.Password, storeId })
            : await client.PinAsync(tenant.DeviceFor(user), user.Login, user.Pin!);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    /// <summary>Вход по PIN с кассы (device = null — без заголовка кассы).</summary>
    public static Task<HttpResponseMessage> PinAsync(this HttpClient client, TestDevice? device, string login, string pin, string? secret = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/pin") { Content = JsonContent.Create(new { login, pin }) };
        if (device is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Device", $"{device.Id}.{secret ?? device.Secret}");
        }

        return client.SendAsync(request);
    }

    public static HttpRequestMessage Get(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    public static HttpRequestMessage PostJson(string url, string? token, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>Меняет claim в payload, подпись оставляет прежней — ровно то, что сделал бы злоумышленник.</summary>
    public static string TamperClaim(string jwt, string claim, string value)
    {
        var parts = jwt.Split('.');
        var payload = JsonNode.Parse(Base64UrlDecode(parts[1]))!.AsObject();
        payload[claim] = value;
        parts[1] = Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return string.Join('.', parts);
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
