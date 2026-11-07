using KassirERP.Application.Purchases;

namespace KassirERP.Api.Endpoints;

public sealed record ReplaceItemsRequest(IReadOnlyList<NewPurchaseItem> Items);

/// <summary>Приём товара (шаг 4). Филиал — из токена.</summary>
internal static class PurchaseEndpoints
{
    public const string WritePolicy = "purchases.write";

    public static void MapPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var purchases = app.MapGroup("/purchases").WithTags("Приход").RequireAuthorization(WritePolicy);

        // Черновик накладной. Повтор того же запроса — 200 с тем же документом, не дубль.
        purchases.MapPost("/", async (NewPurchase request, PurchaseService service, CancellationToken ct) =>
            ToHttp(await service.CreateAsync(request, ct), created: true));

        purchases.MapGet("/{id:guid}", async (Guid id, PurchaseService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } purchase
                ? Results.Ok(purchase)
                : Results.Problem(statusCode: 404, title: "Накладная не найдена"));

        purchases.MapPut("/{id:guid}/items", async (Guid id, ReplaceItemsRequest request, PurchaseService service, CancellationToken ct) =>
            ToHttp(await service.ReplaceItemsAsync(id, request.Items, ct)));

        purchases.MapDelete("/{id:guid}", async (Guid id, PurchaseService service, CancellationToken ct) =>
            ToHttp(await service.DeleteAsync(id, ct)));

        // Проведение: остатки и движения склада. Повторное — 200, второго движения нет.
        purchases.MapPost("/{id:guid}/accept", async (Guid id, PurchaseService service, CancellationToken ct) =>
            ToHttp(await service.AcceptAsync(id, ct)));
    }

    private static IResult ToHttp(PurchaseResult result, bool created = false) => result.Status switch
    {
        PurchaseStatus.Done when result.Purchase is null => Results.NoContent(),
        PurchaseStatus.Done when created => Results.Created($"/purchases/{result.Purchase!.Id}", result.Purchase),
        PurchaseStatus.Done or PurchaseStatus.AlreadyDone => Results.Ok(result.Purchase),
        PurchaseStatus.NotFound => Results.Problem(statusCode: 404, title: "Накладная не найдена"),
        PurchaseStatus.Locked => Results.Problem(statusCode: 409, title: "Накладная проведена", detail: result.Detail),
        PurchaseStatus.Conflict => Results.Problem(statusCode: 409, title: "Конфликт", detail: result.Detail),
        _ => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: result.Detail),
    };
}
