using KassirERP.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KassirERP.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Kassir";

    /// <summary>Подключение к Postgres и health check базы.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName}. " +
                $"Задайте её переменной окружения ConnectionStrings__{ConnectionStringName}.");
        }

        // Один пул соединений на приложение. Регистрация через фабрику —
        // контейнер сам закроет пул при остановке.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready], timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
