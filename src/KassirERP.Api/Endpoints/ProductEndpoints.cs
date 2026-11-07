using KassirERP.Application.Catalog;

namespace KassirERP.Api.Endpoints;

public sealed record AddVariantsRequest(IReadOnlyList<NewVariant> Variants);

internal static class ProductEndpoints
{
    public const string CatalogWritePolicy = "catalog.write";

    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/products").WithTags("Товары").RequireAuthorization();

        // Скан на кассе: пара + остаток в филиале из токена.
        products.MapGet("/barcode/{barcode}", async (string barcode, CatalogService catalog, CancellationToken ct) =>
            await catalog.ScanAsync(barcode, ct) is { } found
                ? Results.Ok(found)
                : Results.Problem(statusCode: 404, title: "Штрихкод не найден"));

        // 50 товаров, чьё название содержит q, со всеми их парами.
        products.MapGet("/search", async (string q, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.SearchAsync(q, ct)));

        products.MapGet("/{id:guid}", async (Guid id, CatalogService catalog, CancellationToken ct) =>
            await catalog.GetAsync(id, ct) is { } product
                ? Results.Ok(product)
                : Results.Problem(statusCode: 404, title: "Товар не найден"));

        products.MapPost("/", async (NewProduct request, CatalogService catalog, CancellationToken ct) =>
            ToHttp(await catalog.CreateAsync(request, ct), $"/products/{request.Id}"))
            .RequireAuthorization(CatalogWritePolicy);

        products.MapPost("/{id:guid}/variants", async (Guid id, AddVariantsRequest request, CatalogService catalog, CancellationToken ct) =>
            ToHttp(await catalog.AddVariantsAsync(id, request.Variants, ct), $"/products/{id}"))
            .RequireAuthorization(CatalogWritePolicy);
    }

    private static IResult ToHttp(WriteResult result, string location) => result.Status switch
    {
        WriteStatus.Created => Results.Created(location, null),
        WriteStatus.NotFound => Results.Problem(statusCode: 404, title: "Товар не найден"),
        WriteStatus.UnknownCategory => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: result.Detail),
        _ => Results.Problem(statusCode: 409, title: "Конфликт", detail: result.Detail),
    };
}
