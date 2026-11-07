using KassirERP.Application.Abstractions;
using KassirERP.Application.Auth;

namespace KassirERP.Application.Devices;

public sealed record EnrollmentCode(string Code, Guid StoreId, DateTimeOffset ExpiresAt);

/// <summary>Секрет показывается ровно один раз — в ответе на регистрацию.</summary>
public sealed record EnrollmentResult(Guid DeviceId, string DeviceSecret, Guid TenantId, Guid StoreId);

public sealed record DeviceView(Guid Id, Guid StoreId, string Name, DateTimeOffset RegisteredAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt);

/// <summary>Кассы фирмы. Все методы — в тенанте из токена (TenantDb).</summary>
public interface IDeviceRegistry
{
    /// <summary>false — такой хеш кода уже есть среди действующих (совпадение 8 цифр), нужно сгенерировать другой.</summary>
    Task<bool> CreateEnrollmentAsync(Guid id, Guid storeId, string codeHash, DateTimeOffset expiresAt, Guid createdBy, CancellationToken ct);

    Task<IReadOnlyList<DeviceView>> ListAsync(Guid? storeId, CancellationToken ct);

    /// <summary>false — кассы нет в этой фирме.</summary>
    Task<bool> RevokeAsync(Guid deviceId, CancellationToken ct);
}

/// <summary>
/// Регистрация касс: менеджер выдаёт одноразовый код для своего филиала,
/// касса обменивает его на свой id и секрет.
/// </summary>
public sealed class DeviceService(IDeviceRegistry registry, IAuthStore authStore, ITenantContext tenant, IRevokedDevices revoked, TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public async Task<EnrollmentCode> CreateEnrollmentCodeAsync(CancellationToken ct)
    {
        var store = tenant.StoreId
            ?? throw new AppValidationException("В токене нет филиала: войдите с выбором филиала, для которого подключаете кассу.");
        var expiresAt = time.GetUtcNow() + CodeLifetime;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = DeviceSecrets.NewEnrollmentCode();
            if (await registry.CreateEnrollmentAsync(Guid.CreateVersion7(), store, DeviceSecrets.Hash(code), expiresAt, tenant.UserId, ct))
            {
                return new EnrollmentCode(code, store, expiresAt);
            }
        }

        throw new InvalidOperationException("Не удалось выдать уникальный код подключения.");
    }

    /// <summary>Касса без токена: код подключения → id и секрет. null — код неверен, истёк или использован.</summary>
    public async Task<EnrollmentResult?> EnrollAsync(string code, string name, CancellationToken ct)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length is 0 or > 100)
        {
            throw new AppValidationException("Название кассы: от 1 до 100 символов.");
        }

        var deviceId = Guid.CreateVersion7();
        var secret = DeviceSecrets.NewSecret();
        var enrolled = await authStore.EnrollDeviceAsync(DeviceSecrets.Hash(code.Trim()), deviceId, trimmedName, DeviceSecrets.Hash(secret), ct);
        return enrolled is null ? null : new EnrollmentResult(deviceId, secret, enrolled.TenantId, enrolled.StoreId);
    }

    public Task<IReadOnlyList<DeviceView>> ListAsync(CancellationToken ct) => registry.ListAsync(tenant.StoreId, ct);

    public async Task<bool> RevokeAsync(Guid deviceId, CancellationToken ct)
    {
        if (!await registry.RevokeAsync(deviceId, ct))
        {
            return false;
        }

        revoked.MarkRevoked(deviceId); // на этом экземпляре — сразу, на остальных — в течение 30 с
        return true;
    }
}
