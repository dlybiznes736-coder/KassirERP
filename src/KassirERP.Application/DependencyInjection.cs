using KassirERP.Application.Auth;
using KassirERP.Application.Catalog;
using KassirERP.Application.Devices;
using KassirERP.Application.Purchases;
using KassirERP.Application.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace KassirERP.Application;

public static class DependencyInjection
{
    /// <summary>Регистрирует сервисы слоя Application.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<AuthService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<PurchaseService>();
        services.AddScoped<SalesService>();
        return services;
    }
}
