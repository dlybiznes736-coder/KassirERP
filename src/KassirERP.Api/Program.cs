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
    .AddPolicy(ProductEndpoints.CatalogWritePolicy, policy => policy.RequireRole(Roles.CatalogWriters))
    .AddPolicy(PurchaseEndpoints.WritePolicy, policy => policy.RequireRole(Roles.CatalogWriters))
    // Смены и чеки — только с кассы: токен, полученный по PIN на зарегистрированной кассе.
    .AddPolicy(SalesEndpoints.DevicePolicy, policy => policy.RequireClaim(JwtClaims.DeviceId))
    // Цены и лист филиала — владелец, администратор, менеджер (кладовщик заводит товар, но не цены).
    .AddPolicy(PricingEndpoints.WritePolicy, policy => policy.RequireRole(Roles.Owner, Roles.Admin, Roles.Manager))
    .AddPolicy(PricingEndpoints.ReadPolicy, policy => policy.RequireRole(Roles.CatalogWriters))
    // Настройки фирмы: доступ к филиалам, весовые префиксы. Кассир и кладовщик — 403.
    // Отчёты: деньги — владелец, администратор, менеджер; склад — и кладовщик. Кассир — 403.
    .AddPolicy(ReportEndpoints.FinancePolicy, policy => policy.RequireRole(Roles.Owner, Roles.Admin, Roles.Manager))
    .AddPolicy(ReportEndpoints.StockPolicy, policy => policy.RequireRole(Roles.CatalogWriters))
    .AddPolicy(AdminEndpoints.ManagePolicy, policy => policy.RequireRole(Roles.Owner, Roles.Admin, Roles.Manager))
    .AddPolicy(TransferEndpoints.WritePolicy, policy => policy.RequireRole(Roles.CatalogWriters))
    .AddPolicy(StockDocumentEndpoints.WritePolicy, policy => policy.RequireRole(Roles.CatalogWriters))
    // Новую кассу подключает только менеджер, вошедший по паролю: с украденной
    // кассы (вход по PIN) выпустить ещё одну кассу нельзя.
    .AddPolicy(DeviceEndpoints.ManagePolicy, policy => policy
        .RequireRole(Roles.Owner, Roles.Admin, Roles.Manager)
        .RequireClaim(JwtClaims.Method, "pwd"));

// --- Лимиты входа поверх блокировки учётки --------------------------------
// По IP — вход по паролю и регистрация кассы (AuthRateLimitOptions). По кассе —
// вход по PIN (PinRateLimitOptions): 50 касс за одним NAT друг другу не мешают.
// За обратным прокси нужен UseForwardedHeaders, иначе все клиенты — один IP прокси.
builder.Services.Configure<AuthRateLimitOptions>(builder.Configuration.GetSection(AuthRateLimitOptions.Section));
builder.Services.Configure<PinRateLimitOptions>(builder.Configuration.GetSection(PinRateLimitOptions.Section));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
        return TokenBucket("ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"), limits.TokenLimit, limits.TokensPerMinute);
    });
    options.AddPolicy(AuthEndpoints.PinRateLimitPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<PinRateLimitOptions>>().Value;
        var key = DeviceHeader.TryParse(context.Request, out var deviceId, out _)
            ? "device:" + deviceId
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return TokenBucket(key, limits.TokenLimit, limits.TokensPerMinute);
    });
    options.OnRejected = (context, ct) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ValueTask(Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Слишком много запросов на вход",
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
app.UseMiddleware<RevokedDeviceGuard>();
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
app.MapDeviceEndpoints();
app.MapProductEndpoints();
app.MapPurchaseEndpoints();
app.MapSalesEndpoints();
app.MapPricingEndpoints();
app.MapTransferEndpoints();
app.MapStockDocumentEndpoints();
app.MapSyncEndpoints();
app.MapAdminEndpoints();
app.MapReportEndpoints();

app.Run();

static RateLimitPartition<string> TokenBucket(string key, int tokenLimit, int tokensPerMinute) =>
    RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(1, tokenLimit),
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / Math.Max(1, tokensPerMinute),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

/// <summary>Точка входа для WebApplicationFactory в тестах.</summary>
public partial class Program;
