using KassirERP.Application.Admin;
using KassirERP.Infrastructure.Persistence;
using Npgsql;

namespace KassirERP.Infrastructure.Admin;

/// <summary>Доступ к филиалам и весовые префиксы через TenantDb (тенант из токена).</summary>
internal sealed class AdminStore(TenantDb db) : IAdminStore
{
    private const string UsersSql =
        """
        SELECT u.id, u.login, u.full_name, u.role, u.store_id, u.is_active,
               ARRAY(SELECT s.store_id FROM user_stores s WHERE s.user_id = u.id ORDER BY s.store_id)
        FROM users u WHERE u.deleted_at IS NULL
        """;

    public Task<IReadOnlyList<UserView>> UsersAsync(CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<UserView>>([new NpgsqlBatchCommand(UsersSql + " ORDER BY u.login")], reader => ReadUsersAsync(reader, ct), ct);

    public async Task<UserView?> UserAsync(Guid userId, CancellationToken ct) =>
        (await db.ReadAsync<IReadOnlyList<UserView>>(
            [new NpgsqlBatchCommand(UsersSql + " AND u.id = $1") { Parameters = { new NpgsqlParameter { Value = userId } } }],
            reader => ReadUsersAsync(reader, ct), ct)).FirstOrDefault();

    public Task<AdminResult> SetUserStoresAsync(Guid userId, IReadOnlyList<Guid> storeIds, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            await using (var user = new NpgsqlCommand("SELECT 1 FROM users WHERE id = $1 AND deleted_at IS NULL FOR UPDATE", connection, tx))
            {
                user.Parameters.Add(new NpgsqlParameter { Value = userId });
                if (await user.ExecuteScalarAsync(ct) is null)
                {
                    return new AdminResult(AdminStatus.NotFound, "Сотрудник не найден.");
                }
            }

            var ids = storeIds.ToArray();
            // Филиалы — только своей фирмы: чужие под RLS не видны (FK проверил бы только существование).
            await using (var stores = new NpgsqlCommand(
                "SELECT u.id FROM unnest($1::uuid[]) u(id) WHERE NOT EXISTS (SELECT 1 FROM stores s WHERE s.id = u.id AND s.deleted_at IS NULL) LIMIT 1",
                connection, tx))
            {
                stores.Parameters.Add(new NpgsqlParameter { Value = ids });
                if (await stores.ExecuteScalarAsync(ct) is Guid missing)
                {
                    return new AdminResult(AdminStatus.Invalid, $"Филиал {missing} не найден.");
                }
            }

            await using (var delete = new NpgsqlCommand("DELETE FROM user_stores WHERE user_id = $1 AND store_id <> ALL($2)", connection, tx))
            {
                delete.Parameters.Add(new NpgsqlParameter { Value = userId });
                delete.Parameters.Add(new NpgsqlParameter { Value = ids });
                await delete.ExecuteNonQueryAsync(ct);
            }

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO user_stores (tenant_id, user_id, store_id)
                SELECT current_tenant_id(), $1, u.id FROM unnest($2::uuid[]) u(id)
                ON CONFLICT (user_id, store_id) DO NOTHING
                """, connection, tx))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = userId });
                insert.Parameters.Add(new NpgsqlParameter { Value = ids });
                await insert.ExecuteNonQueryAsync(ct);
            }

            return new AdminResult(AdminStatus.Done);
        }, ct);

    public Task<IReadOnlyList<string>> WeightPrefixesAsync(CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<string>>([new NpgsqlBatchCommand("SELECT prefix FROM weight_barcode_prefixes ORDER BY prefix")], async reader =>
        {
            var result = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }, ct);

    public Task<AdminResult> SetWeightPrefixesAsync(IReadOnlyList<string> prefixes, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var values = prefixes.ToArray();
            await using (var delete = new NpgsqlCommand("DELETE FROM weight_barcode_prefixes WHERE prefix <> ALL($1)", connection, tx))
            {
                delete.Parameters.Add(new NpgsqlParameter { Value = values });
                await delete.ExecuteNonQueryAsync(ct);
            }

            await using (var insert = new NpgsqlCommand(
                "INSERT INTO weight_barcode_prefixes (tenant_id, prefix) SELECT current_tenant_id(), p FROM unnest($1::text[]) p ON CONFLICT DO NOTHING",
                connection, tx))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = values });
                await insert.ExecuteNonQueryAsync(ct);
            }

            return new AdminResult(AdminStatus.Done);
        }, ct);

    private static async Task<IReadOnlyList<UserView>> ReadUsersAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var users = new List<UserView>();
        while (await reader.ReadAsync(ct))
        {
            users.Add(new UserView(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetBoolean(5), reader.GetFieldValue<Guid[]>(6)));
        }

        return users;
    }
}
