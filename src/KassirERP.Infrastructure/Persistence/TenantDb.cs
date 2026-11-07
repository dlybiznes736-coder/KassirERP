using KassirERP.Application.Abstractions;
using Npgsql;

namespace KassirERP.Infrastructure.Persistence;

/// <summary>
/// ЕДИНСТВЕННАЯ точка, через которую tenant_id попадает в соединение
/// (app.tenant_id → RLS). Значение берётся только из <see cref="ITenantContext"/>,
/// то есть из проверенного JWT. Строка 'app.tenant_id' не должна встречаться
/// больше нигде в src — это проверяет тест ArchitectureTests.
///
/// set_config(..., is_local = true): значение живёт до конца транзакции и не
/// переезжает с соединением обратно в пул к следующему запросу.
/// </summary>
internal sealed class TenantDb(KassirDataSource dataSource, ITenantContext tenant)
{
    // app.user_id — кто действует (журнал цен и другие аудиторские записи в базе);
    // источник тот же — проверенный JWT.
    private const string SetTenantSql = "SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)";

    /// <summary>
    /// Чтение за один сетевой обмен: set_config и запросы уходят одним пакетом,
    /// который Postgres выполняет в одной неявной транзакции.
    /// Читатель получает результаты запросов (результат set_config уже пропущен).
    /// </summary>
    public async Task<T> ReadAsync<T>(IReadOnlyList<NpgsqlBatchCommand> queries, Func<NpgsqlDataReader, Task<T>> read, CancellationToken ct)
    {
        var tenantId = tenant.TenantId; // без проверенного токена — исключение, а не пустой тенант
        var userId = tenant.UserId;
        await using var connection = await dataSource.Inner.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(connection);
        batch.BatchCommands.Add(SetTenantCommand(tenantId, userId));
        foreach (var query in queries)
        {
            batch.BatchCommands.Add(query);
        }

        await using var reader = await batch.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct);
        return await read(reader);
    }

    /// <summary>Запись: явная транзакция, первым оператором — set_config.</summary>
    public async Task<T> WriteAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        var tenantId = tenant.TenantId;
        var userId = tenant.UserId;
        await using var connection = await dataSource.Inner.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            batch.BatchCommands.Add(SetTenantCommand(tenantId, userId));
            await batch.ExecuteNonQueryAsync(ct);
        }

        var result = await work(connection, transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static NpgsqlBatchCommand SetTenantCommand(Guid tenantId, Guid userId) =>
        new(SetTenantSql)
        {
            Parameters =
            {
                new NpgsqlParameter { Value = tenantId.ToString() },
                new NpgsqlParameter { Value = userId.ToString() },
            },
        };
}
