using KassirERP.Application.Stock;

namespace KassirERP.Api.Endpoints;

/// <summary>Списание и инвентаризация в филиале из токена.</summary>
internal static class StockDocumentEndpoints
{
    public const string WritePolicy = "stock.write";

    public static void MapStockDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var writeoffs = app.MapGroup("/writeoffs").WithTags("Списание").RequireAuthorization(WritePolicy);

        // Проводится сразу: остаток уменьшается. Повтор того же запроса — 200, не дубль.
        writeoffs.MapPost("/", async (NewWriteoff request, StockDocumentService service, CancellationToken ct) =>
        {
            var result = await service.CreateWriteoffAsync(request, ct);
            return ToHttp(result.Status, result.Detail, result.Writeoff, $"/writeoffs/{request.Id}");
        });

        writeoffs.MapGet("/{id:guid}", async (Guid id, StockDocumentService service, CancellationToken ct) =>
            await service.GetWriteoffAsync(id, ct) is { } writeoff ? Results.Ok(writeoff) : Results.Problem(statusCode: 404, title: "Списание не найдено"));

        var inventories = app.MapGroup("/inventories").WithTags("Инвентаризация").RequireAuthorization(WritePolicy);

        // Открыть: одна открытая на филиал. Продажи при этом не останавливаются.
        inventories.MapPost("/", async (OpenInventory request, StockDocumentService service, CancellationToken ct) =>
        {
            var result = await service.OpenInventoryAsync(request, ct);
            return ToHttp(result.Status, result.Detail, result.Inventory, $"/inventories/{request.Id}");
        });

        // Подсчёт пачкой; пересчёт пары перезаписывает прежний.
        inventories.MapPost("/{id:guid}/counts", async (Guid id, CountInventory request, StockDocumentService service, CancellationToken ct) =>
        {
            var result = await service.CountAsync(id, request, ct);
            return ToHttp(result.Status, result.Detail, result.Inventory);
        });

        // Проведение: разница counted − book уходит движением склада. Повтор — 200 без второго движения.
        inventories.MapPost("/{id:guid}/post", async (Guid id, StockDocumentService service, CancellationToken ct) =>
        {
            var result = await service.PostInventoryAsync(id, ct);
            return ToHttp(result.Status, result.Detail, result.Inventory);
        });

        inventories.MapGet("/{id:guid}", async (Guid id, StockDocumentService service, CancellationToken ct) =>
            await service.GetInventoryAsync(id, ct) is { } inventory ? Results.Ok(inventory) : Results.Problem(statusCode: 404, title: "Инвентаризация не найдена"));
    }

    private static IResult ToHttp(StockDocStatus status, string? detail, object? body, string? created = null) => status switch
    {
        StockDocStatus.Done when created is not null => Results.Created(created, body),
        StockDocStatus.Done or StockDocStatus.AlreadyDone => Results.Ok(body),
        StockDocStatus.NotFound => Results.Problem(statusCode: 404, title: "Не найдено", detail: detail),
        StockDocStatus.WrongStore => Results.Problem(statusCode: 403, title: "Не ваш филиал", detail: detail),
        StockDocStatus.Conflict or StockDocStatus.WrongState => Results.Problem(statusCode: 409, title: "Конфликт", detail: detail),
        _ => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: detail),
    };
}
