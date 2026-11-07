using KassirERP.Application.Devices;

namespace KassirERP.Api.Endpoints;

public sealed record EnrollRequest(string Code, string Name);

internal static class DeviceEndpoints
{
    /// <summary>Кассы подключает и отключает менеджер, вошедший по паролю (не с кассы по PIN).</summary>
    public const string ManagePolicy = "devices.manage";

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/devices").WithTags("Кассы");

        // Менеджер: одноразовый код подключения для филиала из своего токена.
        devices.MapPost("/enrollment-codes", async (DeviceService service, CancellationToken ct) =>
                Results.Ok(await service.CreateEnrollmentCodeAsync(ct)))
            .RequireAuthorization(ManagePolicy);

        // Касса (без токена): код → id и секрет. Секрет показывается один раз.
        devices.MapPost("/enroll", async (EnrollRequest request, DeviceService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 20 || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Problem(statusCode: 400, title: "Нужны код подключения и название кассы");
            }

            return await service.EnrollAsync(request.Code, request.Name, ct) is { } enrolled
                ? Results.Ok(enrolled)
                : Results.Problem(statusCode: 401, title: "Код подключения неверен, истёк или уже использован");
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        devices.MapGet("/", async (DeviceService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequireAuthorization(ManagePolicy);

        devices.MapPost("/{id:guid}/revoke", async (Guid id, DeviceService service, CancellationToken ct) =>
                await service.RevokeAsync(id, ct) ? Results.NoContent() : Results.Problem(statusCode: 404, title: "Касса не найдена"))
            .RequireAuthorization(ManagePolicy);
    }
}
