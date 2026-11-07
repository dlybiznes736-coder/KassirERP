using KassirERP.Application.Devices;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Devices;

/// <summary>Кассы фирмы: код подключения, список, отключение. Всё через TenantDb (RLS).</summary>
internal sealed class DeviceRegistry(TenantDb db) : IDeviceRegistry
{
    public Task<bool> CreateEnrollmentAsync(Guid id, Guid storeId, string codeHash, DateTimeOffset expiresAt, Guid createdBy, CancellationToken ct) =>
        db.WriteAsync(async (connection, transaction) =>
        {
            // Филиал должен быть виден в своей фирме: FK проверяет существование мимо RLS.
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO device_enrollments (id, tenant_id, store_id, code_hash, expires_at, created_by)
                SELECT $1, current_tenant_id(), s.id, $3, $4, $5 FROM stores s WHERE s.id = $2
                ON CONFLICT (code_hash) WHERE used_at IS NULL DO NOTHING
                """, connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = id });
            insert.Parameters.Add(new NpgsqlParameter { Value = storeId });
            insert.Parameters.Add(new NpgsqlParameter { Value = codeHash });
            insert.Parameters.Add(new NpgsqlParameter { Value = expiresAt });
            insert.Parameters.Add(new NpgsqlParameter { Value = createdBy });
            return await insert.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    public Task<IReadOnlyList<DeviceView>> ListAsync(Guid? storeId, CancellationToken ct)
    {
        var query = new NpgsqlBatchCommand(
            """
            SELECT id, store_id, name, registered_at, last_seen_at, revoked_at FROM devices
            WHERE ($1::uuid IS NULL OR store_id = $1) AND secret_hash <> 'none'
            ORDER BY registered_at
            """)
        {
            Parameters = { new NpgsqlParameter { Value = (object?)storeId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid } },
        };

        return db.ReadAsync<IReadOnlyList<DeviceView>>([query], async reader =>
        {
            var list = new List<DeviceView>();
            while (await reader.ReadAsync(ct))
            {
                list.Add(new DeviceView(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
            }

            return list;
        }, ct);
    }

    public Task<bool> RevokeAsync(Guid deviceId, CancellationToken ct) =>
        db.WriteAsync(async (connection, transaction) =>
        {
            await using var update = new NpgsqlCommand(
                "UPDATE devices SET revoked_at = coalesce(revoked_at, now()) WHERE id = $1", connection, transaction);
            update.Parameters.Add(new NpgsqlParameter { Value = deviceId });
            return await update.ExecuteNonQueryAsync(ct) == 1;
        }, ct);
}
