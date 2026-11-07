using System.Net;
using System.Text;
using KassirERP.Api.Tests.Infrastructure;
using KassirERP.Infrastructure.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KassirERP.Api.Tests;

/// <summary>Просроченный, подделанный и неподписанный токены отклоняются.</summary>
[Collection(ApiCollection.Name)]
public sealed class TokenValidationTests(KassirApiFactory factory)
{
    private readonly TestData _data = factory.Data;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        var token = CreateToken(KassirApiFactory.SigningKey, expires: DateTime.UtcNow.AddMinutes(10));

        var response = await _client.SendAsync(ApiClient.Get("/auth/me", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        // Истёк 5 минут назад — больше допуска на расхождение часов (30 с).
        var token = CreateToken(KassirApiFactory.SigningKey, expires: DateTime.UtcNow.AddMinutes(-5));

        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.A.Barcode}", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_other_key_is_rejected()
    {
        var token = CreateToken("attacker-key-0123456789abcdef-0123456789abcdef", expires: DateTime.UtcNow.AddMinutes(10));

        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.A.Barcode}", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        var header = ApiClient.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = ApiClient.Base64UrlEncode(Encoding.UTF8.GetBytes(
            $$"""{"sub":"{{_data.A.Users["owner"].Id}}","tenant_id":"{{_data.A.Id}}","role":"owner","iss":"kassir-erp","aud":"kassir-erp","exp":{{exp}}}"""));

        var response = await _client.SendAsync(ApiClient.Get("/auth/me", $"{header}.{payload}."));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        var response = await _client.SendAsync(ApiClient.Get($"/products/barcode/{_data.A.Barcode}", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private string CreateToken(string key, DateTime expires)
    {
        var owner = _data.A.Users["cashier"];
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = factory.Jwt.Issuer,
            Audience = factory.Jwt.Audience,
            IssuedAt = expires.AddHours(-1),
            NotBefore = expires.AddHours(-1),
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [JwtClaims.Subject] = owner.Id.ToString(),
                [JwtClaims.TenantId] = _data.A.Id.ToString(),
                [JwtClaims.StoreId] = _data.A.StoreId.ToString(),
                [JwtClaims.Role] = owner.Role,
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });
    }
}
