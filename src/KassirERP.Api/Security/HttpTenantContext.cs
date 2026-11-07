using KassirERP.Application.Abstractions;
using KassirERP.Infrastructure.Auth;

namespace KassirERP.Api.Security;

/// <summary>
/// Тенант, пользователь, филиал и роль — только из claims аутентифицированного
/// пользователя. Единственная схема аутентификации — JwtBearer, то есть claims
/// появляются здесь только после проверки подписи, издателя, аудитории и срока.
/// </summary>
internal sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid TenantId => RequiredGuid(JwtClaims.TenantId);
    public Guid UserId => RequiredGuid(JwtClaims.Subject);
    public Guid? StoreId => Guid.TryParse(Principal.FindFirst(JwtClaims.StoreId)?.Value, out var id) ? id : null;
    public Guid? DeviceId => Guid.TryParse(Principal.FindFirst(JwtClaims.DeviceId)?.Value, out var id) ? id : null;
    public string Role => Principal.FindFirst(JwtClaims.Role)?.Value ?? throw Unauthenticated();

    private System.Security.Claims.ClaimsPrincipal Principal
    {
        get
        {
            var user = accessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? user : throw Unauthenticated();
        }
    }

    private Guid RequiredGuid(string claim) =>
        Guid.TryParse(Principal.FindFirst(claim)?.Value, out var id) && id != Guid.Empty ? id : throw Unauthenticated();

    private static UnauthorizedAccessException Unauthenticated() =>
        new("Нет проверенного токена: тенант неизвестен.");
}
