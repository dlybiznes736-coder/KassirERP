namespace KassirERP.Application.Auth;

public enum CredentialKind
{
    Password,
    Pin,
}

/// <summary>Пользователь, найденный для проверки пароля или PIN (до входа, мимо RLS).</summary>
public sealed record UserCredentials(
    Guid UserId,
    Guid TenantId,
    Guid? StoreId,
    string Role,
    string? PasswordHash,
    string? PinHash);

/// <summary>Касса для входа по PIN (до входа, мимо RLS).</summary>
public sealed record DeviceCredentials(Guid DeviceId, Guid TenantId, Guid StoreId, string SecretHash, bool Revoked);

public sealed record AttemptPermit(bool Allowed, TimeSpan RetryAfter);

/// <summary>Касса, только что зарегистрированная по коду.</summary>
public sealed record EnrolledDevice(Guid TenantId, Guid StoreId);

/// <summary>Доступ к учётным данным до входа. Реализация — функции схемы auth в Postgres.</summary>
public interface IAuthStore
{
    Task<UserCredentials?> FindUserAsync(string tenantCode, string login, CancellationToken ct);

    Task<UserCredentials?> FindUserInTenantAsync(Guid tenantId, string login, CancellationToken ct);

    Task<DeviceCredentials?> FindDeviceAsync(Guid deviceId, CancellationToken ct);

    /// <summary>Погашает код подключения и создаёт кассу. null — кода нет, он истёк или уже использован.</summary>
    Task<EnrolledDevice?> EnrollDeviceAsync(string codeHash, Guid deviceId, string name, string secretHash, CancellationToken ct);

    /// <summary>Резервирует попытку до проверки хеша. Allowed = false — учётка заблокирована.</summary>
    Task<AttemptPermit> BeginAttemptAsync(Guid userId, CredentialKind kind, int maxAttempts, TimeSpan lockDuration, CancellationToken ct);

    Task ResetAttemptsAsync(Guid userId, CredentialKind kind, CancellationToken ct);

    Task<bool> StoreBelongsToTenantAsync(Guid tenantId, Guid storeId, CancellationToken ct);

    /// <summary>Филиал есть в явном списке доступа сотрудника (user_stores).</summary>
    Task<bool> UserHasStoreAsync(Guid userId, Guid storeId, CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string secret);
    bool Verify(string hash, string secret);
}

public sealed record TokenSubject(Guid UserId, Guid TenantId, Guid? StoreId, string Role, CredentialKind Method, Guid? DeviceId = null);

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public interface ITokenIssuer
{
    IssuedToken Issue(TokenSubject subject);
}
