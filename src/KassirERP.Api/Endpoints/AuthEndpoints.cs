using KassirERP.Api.Security;
using KassirERP.Application.Abstractions;
using KassirERP.Application.Auth;

namespace KassirERP.Api.Endpoints;

public sealed record PasswordLoginRequest(string TenantCode, string Login, string Password, Guid? StoreId);

public sealed record PinLoginRequest(string TenantCode, string Login, string Pin);

public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, Guid UserId, Guid TenantId, Guid? StoreId, string Role);

public sealed record MeResponse(Guid UserId, Guid TenantId, Guid? StoreId, string Role);

internal static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

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

        auth.MapPost("/pin", async (PinLoginRequest request, AuthService service, CancellationToken ct) =>
        {
            if (Blank(request.TenantCode, 100) || Blank(request.Login, 100) || !IsPin(request.Pin))
            {
                return Results.Problem(statusCode: 400, title: "Нужны код фирмы, логин и PIN из 4–8 цифр");
            }

            return ToHttp(await service.LoginWithPinAsync(request.TenantCode.Trim(), request.Login.Trim(), request.Pin, ct));
        }).RequireRateLimiting(RateLimitPolicy);

        auth.MapGet("/me", (ITenantContext tenant) =>
            Results.Ok(new MeResponse(tenant.UserId, tenant.TenantId, tenant.StoreId, tenant.Role)))
            .RequireAuthorization();
    }

    private static IResult ToHttp(LoginResult result) => result.Status switch
    {
        LoginStatus.Success => Results.Ok(new LoginResponse(
            result.Token!.AccessToken, "Bearer", result.Token.ExpiresAt,
            result.Subject!.UserId, result.Subject.TenantId, result.Subject.StoreId, result.Subject.Role)),
        LoginStatus.LockedOut => new RetryAfterResult(result.RetryAfter),
        LoginStatus.StoreRequired => Results.Problem(statusCode: 400, title: "Вход по PIN — только для сотрудника, привязанного к филиалу"),
        LoginStatus.InvalidStore => Results.Problem(statusCode: 400, title: "Филиал недоступен"),
        // Один ответ на «нет фирмы», «нет логина» и «неверный пароль»: не подсказываем, что из этого верно.
        _ => Results.Problem(statusCode: 401, title: "Неверный код фирмы, логин или пароль"),
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
