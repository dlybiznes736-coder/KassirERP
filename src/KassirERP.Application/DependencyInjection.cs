using KassirERP.Application.Auth;
using KassirERP.Application.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace KassirERP.Application;

public static class DependencyInjection
{
    /// <summary>Регистрирует сервисы слоя Application.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<AuthService>();
        services.AddScoped<CatalogService>();
        return services;
    }
}
