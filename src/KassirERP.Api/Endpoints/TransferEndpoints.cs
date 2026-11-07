using KassirERP.Application.Transfers;

namespace KassirERP.Api.Endpoints;

/// <summary>Перемещение между филиалами. Филиал — из токена: отправляет отправитель, принимает получатель.</summary>
internal static class TransferEndpoints
{
    public const string WritePolicy = "transfers.write";

    public static void MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var transfers = app.MapGroup("/transfers").WithTags("Перемещения").RequireAuthorization(WritePolicy);

        // Черновик в филиале из токена. Повтор того же запроса — 200, не дубль.
        transfers.MapPost("/", async (NewTransfer request, TransferService service, CancellationToken ct) =>
            ToHttp(await service.CreateAsync(request, ct), $"/transfers/{request.Id}"));

        // Отправка: остаток отправителя уменьшается, документ «в пути». Повтор — 200 без второго списания.
        transfers.MapPost("/{id:guid}/send", async (Guid id, TransferService service, CancellationToken ct) =>
            ToHttp(await service.SendAsync(id, ct)));

        // Приём: остаток получателя растёт на принятое; меньше отправленного — расхождение.
        transfers.MapPost("/{id:guid}/receive", async (Guid id, ReceiveTransfer request, TransferService service, CancellationToken ct) =>
            ToHttp(await service.ReceiveAsync(id, request, ct)));

        transfers.MapGet("/{id:guid}", async (Guid id, TransferService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } transfer ? Results.Ok(transfer) : Results.Problem(statusCode: 404, title: "Перемещение не найдено"));

        // В пути в мой филиал — что предстоит принять.
        transfers.MapGet("/incoming", async (TransferService service, CancellationToken ct) => Results.Ok(await service.IncomingAsync(ct)));
    }

    private static IResult ToHttp(TransferResult result, string? created = null) => result.Status switch
    {
        TransferStatus.Done when created is not null => Results.Created(created, result.Transfer),
        TransferStatus.Done or TransferStatus.AlreadyDone => Results.Ok(result.Transfer),
        TransferStatus.NotFound => Results.Problem(statusCode: 404, title: "Не найдено", detail: result.Detail),
        TransferStatus.WrongStore => Results.Problem(statusCode: 403, title: "Не ваш филиал", detail: result.Detail),
        TransferStatus.Conflict or TransferStatus.WrongState => Results.Problem(statusCode: 409, title: "Конфликт", detail: result.Detail),
        _ => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: result.Detail),
    };
}
