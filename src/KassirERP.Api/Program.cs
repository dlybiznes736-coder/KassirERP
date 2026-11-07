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
using Microsoft.Extensions.Options;

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

// --- Вход: лимит по IP поверх блокировки учётки (расчёт — AuthRateLimitOptions)
// За обратным прокси нужен UseForwardedHeaders, иначе все клиенты — один IP прокси.
builder.Services.Configure<AuthRateLimitOptions>(builder.Configuration.GetSection(AuthRateLimitOptions.Section));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
        var perMinute = Math.Max(1, limits.TokensPerMinute);
        return RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(1, limits.TokenLimit),
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
    options.OnRejected = (context, ct) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ValueTask(Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Слишком много запросов на вход с этого адреса",
            detail: $"Повторите через {seconds} с.").ExecuteAsync(context.HttpContext));
    };
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
