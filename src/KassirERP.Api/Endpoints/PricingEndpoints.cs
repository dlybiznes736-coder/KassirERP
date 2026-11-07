using KassirERP.Application.Pricing;

namespace KassirERP.Api.Endpoints;

/// <summary>
/// Прайс-листы и цены. Меняют владелец, администратор, менеджер (prices.write);
/// смотрят они же и кладовщик (prices.read). Кассир — 403 на всё.
/// </summary>
internal static class PricingEndpoints
{
    public const string WritePolicy = "prices.write";
    public const string ReadPolicy = "prices.read";

    public static void MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var lists = app.MapGroup("/price-lists").WithTags("Прайс-листы").RequireAuthorization(ReadPolicy);

        lists.MapPost("/", async (NewPriceList request, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.CreateAsync(request, ct), $"/price-lists/{request.Id}")).RequireAuthorization(WritePolicy);

        lists.MapGet("/", async (PriceListService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        lists.MapGet("/{id:guid}/items", async (Guid id, PriceListService service, CancellationToken ct) =>
            await service.GetItemsAsync(id, ct) is { } items ? Results.Ok(items) : Results.Problem(statusCode: 404, title: "Прайс-лист не найден"));

        lists.MapPut("/{id:guid}/items", async (Guid id, SetPrices request, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.SetPricesAsync(id, request, ct))).RequireAuthorization(WritePolicy);

        lists.MapPatch("/{id:guid}", async (Guid id, RenamePriceList request, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.RenameAsync(id, request, ct))).RequireAuthorization(WritePolicy);

        // Удалить можно лист, не назначенный ни одному филиалу (иначе 409).
        lists.MapDelete("/{id:guid}", async (Guid id, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.DeleteAsync(id, ct))).RequireAuthorization(WritePolicy);

        // Базовые цены пар (действуют без листа и для пар, которых нет в листе). Журналируются.
        app.MapPut("/base-prices", async (SetBasePrices request, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.SetBasePricesAsync(request, ct))).WithTags("Прайс-листы").RequireAuthorization(WritePolicy);

        lists.MapDelete("/{id:guid}/items/{variantId:guid}", async (Guid id, Guid variantId, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.RemovePriceAsync(id, variantId, ct))).RequireAuthorization(WritePolicy);

        // Журнал: кто, когда, было, стало. Без фильтров — последние 500 изменений фирмы.
        app.MapGet("/price-changes", async (Guid? priceListId, Guid? variantId, PriceListService service, CancellationToken ct) =>
            Results.Ok(await service.ChangesAsync(priceListId, variantId, ct))).WithTags("Прайс-листы").RequireAuthorization(ReadPolicy);

        // Лист филиала; priceListId = null — базовые цены.
        app.MapPut("/stores/{storeId:guid}/price-list", async (Guid storeId, AssignPriceList request, PriceListService service, CancellationToken ct) =>
            ToHttp(await service.AssignToStoreAsync(storeId, request, ct))).WithTags("Прайс-листы").RequireAuthorization(WritePolicy);
    }

    private static IResult ToHttp(PricingResult result, string? created = null) => result.Status switch
    {
        PricingStatus.Done when created is not null => Results.Created(created, result.Body),
        PricingStatus.Done when result.Body is null => Results.NoContent(),
        PricingStatus.Done or PricingStatus.AlreadyDone => Results.Ok(result.Body),
        PricingStatus.NotFound => Results.Problem(statusCode: 404, title: "Не найдено", detail: result.Detail),
        PricingStatus.Conflict => Results.Problem(statusCode: 409, title: "Конфликт", detail: result.Detail),
        _ => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: result.Detail),
    };
}
