using KassirERP.Api.Health;
using KassirERP.Application;
using KassirERP.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Процесс жив. Базу не трогает: если Postgres лёг, перезапуск API не поможет.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

// Готов принимать запросы: Postgres доступен, роль не обходит RLS. 503 — если нет.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthTags.Ready),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Run();
