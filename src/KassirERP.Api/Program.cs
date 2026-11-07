using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using KassirERP.Api.Endpoints;
using KassirERP.Api.Health;
using KassirERP.Api.Security;
using KassirERP.Application;
using KassirERP.Application.Abstractions;
using KassirERP.Domain;
using KassirERP.Infrastructure;
using KassirERP.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

// --- Тенант: только из проверенного JWT -------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

var jwt = JwtOptions.FromConfiguration(builder.Configuration);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // claims как в токене: sub, tenant_id, store_id, role
        options.TokenValidationParameters = jwt.CreateValidationParameters();
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ProductEndpoints.CatalogWritePolicy, policy => policy.RequireRole(Roles.CatalogWriters));

// --- Вход: ограничение частоты по IP поверх блокировки учётки ----------
var authPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 30);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// --- JSON: неизвестное поле в теле (например tenantId) — 400 -----------
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<TenantInputGuard>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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

app.MapAuthEndpoints();
app.MapProductEndpoints();

app.Run();

/// <summary>Точка входа для WebApplicationFactory в тестах.</summary>
public partial class Program;
