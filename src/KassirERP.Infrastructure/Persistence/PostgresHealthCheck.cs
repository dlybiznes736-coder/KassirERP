using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace KassirERP.Infrastructure.Persistence;

/// <summary>
/// Postgres доступен, и приложение подключено правильной ролью.
/// Роль с SUPERUSER или BYPASSRLS обходит Row Level Security (принцип 8 ТЗ),
/// поэтому такое подключение считается неисправным, даже если база отвечает.
/// </summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT current_user,
                       current_setting('server_version'),
                       r.rolsuper OR r.rolbypassrls
                FROM pg_roles r
                WHERE r.rolname = current_user
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            var user = reader.GetString(0);
            var data = new Dictionary<string, object>
            {
                ["user"] = user,
                ["serverVersion"] = reader.GetString(1),
            };

            if (reader.GetBoolean(2))
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    $"Роль «{user}» обходит Row Level Security. Приложение должно подключаться ролью kassir_app.",
                    data: data);
            }

            return HealthCheckResult.Healthy("Postgres доступен", data);
        }
        catch (NpgsqlException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, $"Postgres недоступен: {ex.Message}", ex);
        }
    }
}
