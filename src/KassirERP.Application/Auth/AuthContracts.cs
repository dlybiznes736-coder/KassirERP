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

public sealed record AttemptPermit(bool Allowed, TimeSpan RetryAfter);

/// <summary>Доступ к учётным данным до входа. Реализация — функции схемы auth в Postgres.</summary>
public interface IAuthStore
{
    Task<UserCredentials?> FindUserAsync(string tenantCode, string login, CancellationToken ct);

    /// <summary>Резервирует попытку до проверки хеша. Allowed = false — учётка заблокирована.</summary>
    Task<AttemptPermit> BeginAttemptAsync(Guid userId, CredentialKind kind, int maxAttempts, TimeSpan lockDuration, CancellationToken ct);

    Task ResetAttemptsAsync(Guid userId, CredentialKind kind, CancellationToken ct);

    Task<bool> StoreBelongsToTenantAsync(Guid tenantId, Guid storeId, CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string secret);
    bool Verify(string hash, string secret);
}

public sealed record TokenSubject(Guid UserId, Guid TenantId, Guid? StoreId, string Role, CredentialKind Method);

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public interface ITokenIssuer
{
    IssuedToken Issue(TokenSubject subject);
}
