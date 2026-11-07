using KassirERP.Application.Auth;
using KassirERP.Infrastructure.Persistence;
using Npgsql;

namespace KassirERP.Infrastructure.Auth;

/// <summary>
/// Вход до появления токена. Тенант в соединении не задан (его ещё не из чего
/// взять), поэтому вызываются только функции схемы auth (SECURITY DEFINER):
/// ни одной таблицы напрямую.
/// </summary>
internal sealed class AuthStore(KassirDataSource dataSource) : IAuthStore
{
    public Task<UserCredentials?> FindUserAsync(string tenantCode, string login, CancellationToken ct) =>
        ReadUserAsync("SELECT user_id, tenant_id, store_id, role, password_hash, pin_hash FROM auth.find_user($1, $2)", tenantCode, login, ct);

    public Task<UserCredentials?> FindUserInTenantAsync(Guid tenantId, string login, CancellationToken ct) =>
        ReadUserAsync("SELECT user_id, tenant_id, store_id, role, password_hash, pin_hash FROM auth.find_user_in_tenant($1, $2)", tenantId, login, ct);

    public async Task<DeviceCredentials?> FindDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand(
            "SELECT device_id, tenant_id, store_id, secret_hash, revoked FROM auth.find_device($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = deviceId });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new DeviceCredentials(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetBoolean(4));
    }

    public async Task<EnrolledDevice?> EnrollDeviceAsync(string codeHash, Guid deviceId, string name, string secretHash, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand(
            "SELECT tenant_id, store_id FROM auth.enroll_device($1, $2, $3, $4)");
        command.Parameters.Add(new NpgsqlParameter { Value = codeHash });
        command.Parameters.Add(new NpgsqlParameter { Value = deviceId });
        command.Parameters.Add(new NpgsqlParameter { Value = name });
        command.Parameters.Add(new NpgsqlParameter { Value = secretHash });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new EnrolledDevice(reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    public async Task<AttemptPermit> BeginAttemptAsync(Guid userId, CredentialKind kind, int maxAttempts, TimeSpan lockDuration, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand(
            "SELECT allowed, retry_after_seconds FROM auth.begin_attempt($1, $2, $3, $4)");
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = KindName(kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = maxAttempts });
        command.Parameters.Add(new NpgsqlParameter { Value = (int)lockDuration.TotalSeconds });
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new AttemptPermit(reader.GetBoolean(0), TimeSpan.FromSeconds(reader.GetInt32(1)));
    }

    public async Task ResetAttemptsAsync(Guid userId, CredentialKind kind, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand("SELECT auth.reset_attempts($1, $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = KindName(kind) });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> StoreBelongsToTenantAsync(Guid tenantId, Guid storeId, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand("SELECT auth.store_in_tenant($1, $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = storeId });
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> UserHasStoreAsync(Guid userId, Guid storeId, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand("SELECT auth.user_has_store($1, $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = storeId });
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<UserCredentials?> ReadUserAsync(string sql, object key, string login, CancellationToken ct)
    {
        await using var command = dataSource.Inner.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = login });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new UserCredentials(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static string KindName(CredentialKind kind) => kind == CredentialKind.Pin ? "pin" : "password";
}
