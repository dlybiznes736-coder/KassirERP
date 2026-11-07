using KassirERP.Application.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KassirERP.Infrastructure.Auth;

internal sealed class JwtTokenIssuer(JwtOptions options, TimeProvider time) : ITokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(options.CreateSigningKey(), SecurityAlgorithms.HmacSha256);

    public IssuedToken Issue(TokenSubject subject)
    {
        var now = time.GetUtcNow();
        var expires = now + (subject.Method == CredentialKind.Pin ? options.PinTokenLifetime : options.PasswordTokenLifetime);

        var claims = new Dictionary<string, object>
        {
            [JwtClaims.Subject] = subject.UserId.ToString(),
            [JwtClaims.TenantId] = subject.TenantId.ToString(),
            [JwtClaims.Role] = subject.Role,
            [JwtClaims.Method] = subject.Method == CredentialKind.Pin ? "pin" : "pwd",
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
        };
        if (subject.StoreId is { } store)
        {
            claims[JwtClaims.StoreId] = store.ToString();
        }

        if (subject.DeviceId is { } device)
        {
            claims[JwtClaims.DeviceId] = device.ToString();
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = _credentials,
        });

        return new IssuedToken(token, expires);
    }
}
