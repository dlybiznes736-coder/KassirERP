using KassirERP.Application.Auth;
using KassirERP.Application.Catalog;
using KassirERP.Infrastructure.Auth;
using KassirERP.Infrastructure.Catalog;
using KassirERP.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KassirERP.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Kassir";

    /// <summary>Postgres, вход (пароль/PIN, JWT), товары, health check.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName}. " +
                $"Задайте её переменной окружения ConnectionStrings__{ConnectionStringName}.");
        }

        // Один пул соединений на приложение. Сам NpgsqlDataSource в DI не попадает —
        // только внутренняя обёртка (см. KassirDataSource).
        services.AddSingleton(_ => new KassirDataSource(NpgsqlDataSource.Create(connectionString)));
        services.AddScoped<TenantDb>();

        services.AddSingleton(JwtOptions.FromConfiguration(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IAuthStore, AuthStore>();
        services.AddScoped<IProductCatalog, ProductCatalog>();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready], timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
