using KassirERP.Application.Devices;

namespace KassirERP.Application.Auth;

public enum LoginStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    /// <summary>Касса неизвестна, отключена или её секрет неверен.</summary>
    InvalidDevice,
    /// <summary>Филиала кассы нет в списке доступа кассира (или список пуст).</summary>
    StoreMismatch,
    /// <summary>Выбранный филиал не принадлежит фирме или недоступен пользователю.</summary>
    InvalidStore,
}

public sealed record LoginResult(LoginStatus Status, IssuedToken? Token = null, TimeSpan RetryAfter = default, TokenSubject? Subject = null);

/// <summary>Вход по паролю (веб) и по PIN (только с зарегистрированной кассы).</summary>
public sealed class AuthService(IAuthStore store, IPasswordHasher hasher, ITokenIssuer tokens)
{
    public const int MaxPasswordAttempts = 10;
    public const int MaxPinAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    // Хеш-пустышка: для несуществующего пользователя тоже считаем PBKDF2,
    // чтобы по времени ответа нельзя было узнать, есть ли такой логин.
    private readonly Lazy<string> _dummyHash = new(() => hasher.Hash("dummy-" + Guid.NewGuid()));

    public async Task<LoginResult> LoginWithPasswordAsync(string tenantCode, string login, string password, Guid? storeId, CancellationToken ct)
    {
        var user = await store.FindUserAsync(tenantCode, login, ct);
        var checkedUser = await CheckSecretAsync(user, user?.PasswordHash, password, CredentialKind.Password, MaxPasswordAttempts, ct);
        if (checkedUser.Status != LoginStatus.Success)
        {
            return checkedUser;
        }

        Guid? tokenStore = user!.StoreId;
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

        return Issue(new TokenSubject(user.UserId, user.TenantId, tokenStore, user.Role, CredentialKind.Password));
    }

    /// <summary>
    /// Вход по PIN. Сначала касса: без её секрета PIN не проверяется вообще,
    /// поэтому перебирать PIN можно только с самой кассы. Фирма и филиал — от кассы.
    /// </summary>
    public async Task<LoginResult> LoginWithPinAsync(Guid deviceId, string deviceSecret, string login, string pin, CancellationToken ct)
    {
        var device = await store.FindDeviceAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceSecrets.Matches(device.SecretHash, deviceSecret))
        {
            return new(LoginStatus.InvalidDevice);
        }

        var user = await store.FindUserInTenantAsync(device.TenantId, login, ct);
        var checkedUser = await CheckSecretAsync(user, user?.PinHash, pin, CredentialKind.Pin, MaxPinAttempts, ct);
        if (checkedUser.Status != LoginStatus.Success)
        {
            return checkedUser;
        }

        // Доступ к филиалам — явный список (user_stores). Пустой список — вход
        // запрещён: «без привязки» больше не значит «все филиалы».
        if (!await store.UserHasStoreAsync(user!.UserId, device.StoreId, ct))
        {
            return new(LoginStatus.StoreMismatch);
        }

        return Issue(new TokenSubject(user.UserId, device.TenantId, device.StoreId, user.Role, CredentialKind.Pin, device.DeviceId));
    }

    private async Task<LoginResult> CheckSecretAsync(UserCredentials? user, string? hash, string secret, CredentialKind kind, int maxAttempts, CancellationToken ct)
    {
        if (user is null || hash is null)
        {
            hasher.Verify(_dummyHash.Value, secret);
            return new(LoginStatus.InvalidCredentials);
        }

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
        return new(LoginStatus.Success);
    }

    private LoginResult Issue(TokenSubject subject) => new(LoginStatus.Success, tokens.Issue(subject), Subject: subject);
}
