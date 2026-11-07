using KassirERP.Application.Abstractions;
using KassirERP.Infrastructure.Auth;

namespace KassirERP.Api.Security;

/// <summary>
/// Токен кассы, которую отключили, отклоняется сразу, а не через 12 часов,
/// когда истечёт. Список отключённых — в памяти (IRevokedDevices).
/// </summary>
internal sealed class RevokedDeviceGuard(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IRevokedDevices revoked)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(JwtClaims.DeviceId)?.Value, out var deviceId)
            && revoked.IsRevoked(deviceId))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Касса отключена",
                detail: "Касса отключена менеджером. Подключите её заново.").ExecuteAsync(context);
        }

        return next(context);
    }
}
