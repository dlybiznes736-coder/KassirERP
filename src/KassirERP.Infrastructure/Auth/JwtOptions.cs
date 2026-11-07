using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace KassirERP.Infrastructure.Auth;

/// <summary>Настройки JWT (секция Jwt). Выпуск и проверка токена берут параметры отсюда — в одном месте.</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "kassir-erp";
    public string Audience { get; set; } = "kassir-erp";

    /// <summary>Ключ HMAC-SHA256, не короче 32 символов. В проде — только из переменной окружения Jwt__SigningKey.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Вход по паролю (веб).</summary>
    public TimeSpan PasswordTokenLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Вход по PIN (касса) — на смену.</summary>
    public TimeSpan PinTokenLifetime { get; set; } = TimeSpan.FromHours(12);

    public static JwtOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration.GetSection(Section).Get<JwtOptions>() ?? new JwtOptions();
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "Не задан ключ подписи JWT или он короче 32 символов: Jwt:SigningKey (переменная окружения Jwt__SigningKey).");
        }

        return options;
    }

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = CreateSigningKey(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // alg=none и чужие алгоритмы отклоняются
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtClaims.Subject,
        RoleClaimType = JwtClaims.Role,
    };
}

public static class JwtClaims
{
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string StoreId = "store_id";
    public const string Role = "role";
    public const string Method = "amr";
    public const string DeviceId = "device_id";
}
