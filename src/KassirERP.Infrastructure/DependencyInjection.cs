using KassirERP.Application.Abstractions;
using KassirERP.Application.Auth;
using KassirERP.Application.Catalog;
using KassirERP.Application.Devices;
using KassirERP.Application.Purchases;
using KassirERP.Infrastructure.Auth;
using KassirERP.Infrastructure.Catalog;
using KassirERP.Infrastructure.Devices;
using KassirERP.Infrastructure.Persistence;
using KassirERP.Infrastructure.Purchases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KassirERP.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Kassir";

    /// <summary>Потолок пула соединений к Postgres на экземпляр API, если в строке подключения не задан.</summary>
    public const int DefaultMaxPoolSize = 30;

    /// <summary>Postgres, вход (пароль/PIN, JWT), кассы, товары, приход, health check.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName}. " +
                $"Задайте её переменной окружения ConnectionStrings__{ConnectionStringName}.");
        }

        // Потолок пула. По умолчанию у Npgsql 100 — ровно max_connections Postgres:
        // всплеск запросов к одному экземпляру API занимал все соединения базы,
        // и новые получали «too many clients» (500) вместо ожидания. С потолком
        // лишние запросы ждут свободное соединение. 30 — с большим запасом: скан
        // держит соединение ≈ 1 мс, поиск ≈ 2 мс. Явное значение в строке
        // подключения (Maximum Pool Size=…) важнее этого.
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase))
        {
            csb.MaxPoolSize = DefaultMaxPoolSize;
        }

        // Один пул соединений на приложение. Сам NpgsqlDataSource в DI не попадает —
        // только внутренняя обёртка (см. KassirDataSource).
        var poolConnectionString = csb.ConnectionString;
        services.AddSingleton(_ => new KassirDataSource(NpgsqlDataSource.Create(poolConnectionString)));
        services.AddScoped<TenantDb>();

        services.AddSingleton(JwtOptions.FromConfiguration(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IAuthStore, AuthStore>();

        services.AddSingleton<RevokedDeviceCache>();
        services.AddSingleton<IRevokedDevices>(sp => sp.GetRequiredService<RevokedDeviceCache>());
        services.AddHostedService(sp => sp.GetRequiredService<RevokedDeviceCache>());
        services.AddScoped<IDeviceRegistry, DeviceRegistry>();

        services.AddScoped<IProductCatalog, ProductCatalog>();
        services.AddScoped<IPurchaseStore, PurchaseStore>();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready], timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
