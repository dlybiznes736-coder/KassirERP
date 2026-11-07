namespace KassirERP.Application.Auth;

public enum LoginStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    /// <summary>Вход по PIN — только для сотрудника, привязанного к филиалу.</summary>
    StoreRequired,
    /// <summary>Выбранный филиал не принадлежит фирме или недоступен пользователю.</summary>
    InvalidStore,
}

public sealed record LoginResult(LoginStatus Status, IssuedToken? Token = null, TimeSpan RetryAfter = default, TokenSubject? Subject = null);

/// <summary>Вход по паролю (веб) и по PIN (касса).</summary>
public sealed class AuthService(IAuthStore store, IPasswordHasher hasher, ITokenIssuer tokens)
{
    public const int MaxPasswordAttempts = 10;
    public const int MaxPinAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    // Хеш-пустышка: для несуществующего пользователя тоже считаем PBKDF2,
    // чтобы по времени ответа нельзя было узнать, есть ли такой логин.
    private readonly Lazy<string> _dummyHash = new(() => hasher.Hash("dummy-" + Guid.NewGuid()));

    public Task<LoginResult> LoginWithPasswordAsync(string tenantCode, string login, string password, Guid? storeId, CancellationToken ct) =>
        LoginAsync(tenantCode, login, password, CredentialKind.Password, storeId, ct);

    public Task<LoginResult> LoginWithPinAsync(string tenantCode, string login, string pin, CancellationToken ct) =>
        LoginAsync(tenantCode, login, pin, CredentialKind.Pin, storeId: null, ct);

    private async Task<LoginResult> LoginAsync(string tenantCode, string login, string secret, CredentialKind kind, Guid? storeId, CancellationToken ct)
    {
        var user = await store.FindUserAsync(tenantCode, login, ct);
        var hash = kind == CredentialKind.Pin ? user?.PinHash : user?.PasswordHash;
        if (user is null || hash is null)
        {
            hasher.Verify(_dummyHash.Value, secret);
            return new(LoginStatus.InvalidCredentials);
        }

        var maxAttempts = kind == CredentialKind.Pin ? MaxPinAttempts : MaxPasswordAttempts;
        var permit = await store.BeginAttemptAsync(user.UserId, kind, maxAttempts, LockDuration, ct);
        if (!permit.Allowed)
        {
            return new(LoginStatus.LockedOut, RetryAfter: permit.RetryAfter);
        }

        if (!hasher.Verify(hash, secret))
        {
            return new(LoginStatus.InvalidCredentials);
        }

        await store.ResetAttemptsAsync(user.UserId, kind, ct);

        Guid? tokenStore = user.StoreId;
        if (kind == CredentialKind.Pin && user.StoreId is null)
        {
            return new(LoginStatus.StoreRequired);
        }

        if (storeId is { } requested && requested != user.StoreId)
        {
            // Привязанный к филиалу сотрудник другой выбрать не может;
            // сотрудник без привязки — только филиал своей фирмы.
            if (user.StoreId is not null || !await store.StoreBelongsToTenantAsync(user.TenantId, requested, ct))
            {
                return new(LoginStatus.InvalidStore);
            }

            tokenStore = requested;
        }

        var subject = new TokenSubject(user.UserId, user.TenantId, tokenStore, user.Role, kind);
        return new(LoginStatus.Success, tokens.Issue(subject), Subject: subject);
    }
}
