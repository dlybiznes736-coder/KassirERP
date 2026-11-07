using KassirERP.Application.Sales;

namespace KassirERP.Api.Endpoints;

/// <summary>Смена и чек (шаг 5). Пишет только касса: токен, полученный по PIN на зарегистрированной кассе.</summary>
internal static class SalesEndpoints
{
    public const string DevicePolicy = "sales.device";

    public static void MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var shifts = app.MapGroup("/shifts").WithTags("Смены").RequireAuthorization(DevicePolicy);

        shifts.MapPost("/", async (NewShift request, SalesService service, CancellationToken ct) =>
            ToHttp(await service.OpenShiftAsync(request, ct), created: $"/shifts/{request.Id}"));

        shifts.MapPost("/{id:guid}/close", async (Guid id, CloseShift request, SalesService service, CancellationToken ct) =>
            ToHttp(await service.CloseShiftAsync(id, request, ct)));

        shifts.MapGet("/{id:guid}", async (Guid id, SalesService service, CancellationToken ct) =>
            await service.GetShiftAsync(id, ct) is { } shift ? Results.Ok(shift) : Results.Problem(statusCode: 404, title: "Смена не найдена"));

        var sales = app.MapGroup("/sales").WithTags("Чеки");

        // Чек или возврат (type = "return"). Повтор того же чека — 200, не дубль.
        sales.MapPost("/", async (NewSale request, SalesService service, CancellationToken ct) =>
                ToHttp(await service.CreateSaleAsync(request, ct), created: $"/sales/{request.Id}"))
            .RequireAuthorization(DevicePolicy);

        sales.MapGet("/{id:guid}", async (Guid id, SalesService service, CancellationToken ct) =>
                await service.GetSaleAsync(id, ct) is { } sale ? Results.Ok(sale) : Results.Problem(statusCode: 404, title: "Чек не найден"))
            .RequireAuthorization();
    }

    private static IResult ToHttp(SaleResult result, string? created = null)
    {
        object? body = (object?)result.Sale ?? result.Shift;
        return result.Status switch
        {
            SaleStatus.Done when created is not null => Results.Created(created, body),
            SaleStatus.Done or SaleStatus.AlreadyDone => Results.Ok(body),
            SaleStatus.NotFound => Results.Problem(statusCode: 404, title: "Не найдено", detail: result.Detail),
            SaleStatus.Conflict => Results.Problem(statusCode: 409, title: "Конфликт", detail: result.Detail),
            SaleStatus.ShiftClosed => Results.Problem(statusCode: 409, title: "Смена закрыта", detail: result.Detail),
            SaleStatus.ReturnExceedsSold => Results.Problem(statusCode: 400, title: "Возврат больше проданного", detail: result.Detail),
            _ => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: result.Detail),
        };
    }
}
