using KassirERP.Application.Sync;

namespace KassirERP.Api.Endpoints;

/// <summary>
/// Синхронизация кассы (шаг 6). Только с кассы: токен, полученный по PIN на
/// зарегистрированной кассе (та же политика, что у чеков).
/// </summary>
internal static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/sync").WithTags("Синхронизация").RequireAuthorization(SalesEndpoints.DevicePolicy);

        // Пачка документов: результат по каждому (accepted, duplicate, rejected, retry).
        // Ответ 200, даже если часть документов отклонена: пачка разобрана целиком.
        sync.MapPost("/push", async (SyncPush request, SyncService service, CancellationToken ct) =>
            Results.Ok(await service.PushAsync(request, ct)));

        // Изменения после курсора (after — из next прошлого ответа; без него — всё).
        sync.MapGet("/products", async (string? after, int? limit, SyncService service, CancellationToken ct) =>
            Results.Ok(await service.ProductsAsync(after, limit, ct)));

        sync.MapGet("/variants", async (string? after, int? limit, SyncService service, CancellationToken ct) =>
            Results.Ok(await service.VariantsAsync(after, limit, ct)));

        // Цены листа филиала из токена. reset = true — лист сменился: сбросить цены листа.
        sync.MapGet("/prices", async (string? after, int? limit, SyncService service, CancellationToken ct) =>
            Results.Ok(await service.PricesAsync(after, limit, ct)));
    }
}
