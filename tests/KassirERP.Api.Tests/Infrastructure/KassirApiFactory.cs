using KassirERP.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace KassirERP.Api.Tests.Infrastructure;

/// <summary>
/// API на настоящей базе (docker compose, порт 15432). Перед тестами создаёт две
/// фирмы A и B со случайным суффиксом, после — удаляет всё, что у них есть.
/// Строки подключения можно переопределить: KASSIR_TEST_APP_CS, KASSIR_TEST_ADMIN_CS.
/// </summary>
public sealed class KassirApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "test-signing-key-0123456789abcdef-0123456789";

    public static readonly string AppConnectionString = Env("KASSIR_TEST_APP_CS",
        "Host=localhost;Port=15432;Database=kassir;Username=kassir_app;Password=kassir_app_dev");

    public static readonly string AdminConnectionString = Env("KASSIR_TEST_ADMIN_CS",
        "Host=localhost;Port=15432;Database=kassir;Username=postgres;Password=postgres_dev");

    public TestData Data { get; } = TestData.Create();

    public JwtOptions Jwt { get; } = new() { SigningKey = SigningKey };

    static KassirApiFactory()
    {
        // Program читает настройки при сборке хоста — переменные окружения видны ему наверняка.
        Environment.SetEnvironmentVariable("ConnectionStrings__Kassir", AppConnectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("RateLimits__AuthPerMinute", "100000");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

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
