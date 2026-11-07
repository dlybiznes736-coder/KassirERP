using KassirERP.Api.Security;
using KassirERP.Application.Abstractions;
using KassirERP.Application.Auth;

namespace KassirERP.Api.Endpoints;

public sealed record PasswordLoginRequest(string TenantCode, string Login, string Password, Guid? StoreId);

/// <summary>Вход по PIN: касса — в заголовке Authorization: Device &lt;id&gt;.&lt;secret&gt;; кода фирмы нет.</summary>
public sealed record PinLoginRequest(string Login, string Pin);

public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, Guid UserId, Guid TenantId, Guid? StoreId, string Role, Guid? DeviceId);

public sealed record MeResponse(Guid UserId, Guid TenantId, Guid? StoreId, string Role, Guid? DeviceId);

internal static class AuthEndpoints
{
    /// <summary>Лимит по IP: вход по паролю, регистрация кассы.</summary>
    public const string RateLimitPolicy = "auth";

    /// <summary>Лимит по кассе: вход по PIN.</summary>
    public const string PinRateLimitPolicy = "pin";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Вход");

        auth.MapPost("/login", async (PasswordLoginRequest request, AuthService service, CancellationToken ct) =>
        {
            if (Blank(request.TenantCode, 100) || Blank(request.Login, 100) || Blank(request.Password, 200))
            {
                return Results.Problem(statusCode: 400, title: "Нужны код фирмы, логин и пароль");
            }

            return ToHttp(await service.LoginWithPasswordAsync(request.TenantCode.Trim(), request.Login.Trim(), request.Password, request.StoreId, ct));
        }).RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/pin", async (PinLoginRequest request, HttpRequest http, AuthService service, CancellationToken ct) =>
        {
            // Без кассы PIN не проверяется вовсе: перебирать PIN можно только с самой кассы.
            if (!DeviceHeader.TryParse(http, out var deviceId, out var deviceSecret))
            {
                return Results.Problem(statusCode: 401, title: "Вход по PIN — только с зарегистрированной кассы",
                    detail: "Нужен заголовок Authorization: Device <deviceId>.<deviceSecret>.");
            }

            if (Blank(request.Login, 100) || !IsPin(request.Pin))
            {
                return Results.Problem(statusCode: 400, title: "Нужны логин и PIN из 4–8 цифр");
            }

            return ToHttp(await service.LoginWithPinAsync(deviceId, deviceSecret, request.Login.Trim(), request.Pin, ct));
        }).RequireRateLimiting(PinRateLimitPolicy);

        auth.MapGet("/me", (ITenantContext tenant) =>
            Results.Ok(new MeResponse(tenant.UserId, tenant.TenantId, tenant.StoreId, tenant.Role, tenant.DeviceId)))
            .RequireAuthorization();
    }

    private static IResult ToHttp(LoginResult result) => result.Status switch
    {
        LoginStatus.Success => Results.Ok(new LoginResponse(
            result.Token!.AccessToken, "Bearer", result.Token.ExpiresAt,
            result.Subject!.UserId, result.Subject.TenantId, result.Subject.StoreId, result.Subject.Role, result.Subject.DeviceId)),
        LoginStatus.LockedOut => new RetryAfterResult(result.RetryAfter),
        LoginStatus.InvalidDevice => Results.Problem(statusCode: 401, title: "Касса не зарегистрирована или отключена"),
        LoginStatus.StoreMismatch => Results.Problem(statusCode: 403, title: "Кассир не работает в филиале этой кассы"),
        LoginStatus.InvalidStore => Results.Problem(statusCode: 400, title: "Филиал недоступен"),
        // Один ответ на «нет фирмы», «нет логина» и «неверный пароль»: не подсказываем, что из этого верно.
        _ => Results.Problem(statusCode: 401, title: "Неверный логин, пароль или PIN"),
    };

    private static bool Blank(string? value, int max) => string.IsNullOrWhiteSpace(value) || value.Length > max;

    private static bool IsPin(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    private sealed class RetryAfterResult(TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Слишком много неверных попыток",
                detail: $"Вход заблокирован, повторите через {seconds} с.").ExecuteAsync(context);
        }
    }
}
