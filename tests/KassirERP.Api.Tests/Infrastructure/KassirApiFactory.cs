using KassirERP.Api.Security;
using KassirERP.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KassirERP.Api.Tests.Infrastructure;

/// <summary>
/// API на настоящей базе (docker compose, порт 15432). Перед тестами создаёт две
/// фирмы A и B со случайным суффиксом, после — удаляет всё, что у них есть.
/// Строки подключения можно переопределить: KASSIR_TEST_APP_CS, KASSIR_TEST_ADMIN_CS.
/// </summary>
public class KassirApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "test-signing-key-0123456789abcdef-0123456789";

    public static readonly string AppConnectionString = Env("KASSIR_TEST_APP_CS",
        "Host=localhost;Port=15432;Database=kassir;Username=kassir_app;Password=kassir_app_dev;Maximum Pool Size=20"); // три фикстуры параллельно: 3 × 20 < max_connections

    public static readonly string AdminConnectionString = Env("KASSIR_TEST_ADMIN_CS",
        "Host=localhost;Port=15432;Database=kassir;Username=postgres;Password=postgres_dev");

    public KassirApiFactory() => Data = TestData.Create(ExtraCashiers);

    public TestData Data { get; }

    /// <summary>Сколько дополнительных кассиров (cashier-01…) завести в фирме A.</summary>
    protected virtual int ExtraCashiers => 0;

    /// <summary>Лимит запросов на вход по паролю и регистрацию кассы (по IP). В тестах — практически без лимита.</summary>
    protected virtual void ConfigureAuthRateLimit(AuthRateLimitOptions options)
    {
        options.TokenLimit = 1_000_000;
        options.TokensPerMinute = 1_000_000;
    }

    /// <summary>Лимит входа по PIN (по кассе). В тестах — практически без лимита.</summary>
    protected virtual void ConfigurePinRateLimit(PinRateLimitOptions options)
    {
        options.TokenLimit = 1_000_000;
        options.TokensPerMinute = 1_000_000;
    }

    public JwtOptions Jwt { get; } = new() { SigningKey = SigningKey };

    static KassirApiFactory()
    {
        // Program читает настройки при сборке хоста — переменные окружения видны ему наверняка.
        Environment.SetEnvironmentVariable("ConnectionStrings__Kassir", AppConnectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.Configure<AuthRateLimitOptions>(ConfigureAuthRateLimit);
            services.Configure<PinRateLimitOptions>(ConfigurePinRateLimit);
        });
    }

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await Data.InsertAsync(connection, new Pbkdf2PasswordHasher());
    }

    public new async Task DisposeAsync()
    {
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await Data.DeleteAsync(connection);
        }

        await base.DisposeAsync();
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<KassirApiFactory>
{
    public const string Name = "api";
}

/// <summary>Боевые лимиты входа (значения по умолчанию) и 50 кассиров с 50 кассами в фирме A.</summary>
public sealed class ProductionRateLimitFactory : KassirApiFactory
{
    public const int Cashiers = 50;

    protected override int ExtraCashiers => Cashiers;

    // Ничего не меняем: проверяются значения по умолчанию.
    protected override void ConfigureAuthRateLimit(AuthRateLimitOptions options)
    {
    }

    protected override void ConfigurePinRateLimit(PinRateLimitOptions options)
    {
    }
}

/// <summary>Крошечные лимиты: 5 запросов по IP и 3 входа по PIN с одной кассы, пополнение 1 в минуту.</summary>
public sealed class TinyRateLimitFactory : KassirApiFactory
{
    public const int Limit = 5;
    public const int PinLimit = 3;

    protected override void ConfigureAuthRateLimit(AuthRateLimitOptions options)
    {
        options.TokenLimit = Limit;
        options.TokensPerMinute = 1;
    }

    protected override void ConfigurePinRateLimit(PinRateLimitOptions options)
    {
        options.TokenLimit = PinLimit;
        options.TokensPerMinute = 1;
    }
}
