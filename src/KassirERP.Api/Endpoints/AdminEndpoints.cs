using KassirERP.Application.Admin;

namespace KassirERP.Api.Endpoints;

/// <summary>
/// Настройки фирмы вместо SQL: доступ сотрудников к филиалам и весовые префиксы.
/// Владелец, администратор, менеджер; кассир и кладовщик — 403.
/// </summary>
internal static class AdminEndpoints
{
    public const string ManagePolicy = "settings.manage";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/users").WithTags("Сотрудники").RequireAuthorization(ManagePolicy);

        users.MapGet("/", async (AdminService service, CancellationToken ct) => Results.Ok(await service.UsersAsync(ct)));

        users.MapGet("/{id:guid}", async (Guid id, AdminService service, CancellationToken ct) =>
            await service.UserAsync(id, ct) is { } user ? Results.Ok(user) : Results.Problem(statusCode: 404, title: "Сотрудник не найден"));

        // Филиалы, в которые сотрудник входит по PIN. Список заменяется целиком; пустой — по PIN никуда.
        users.MapPut("/{id:guid}/stores", async (Guid id, SetUserStores request, AdminService service, CancellationToken ct) =>
        {
            var result = await service.SetUserStoresAsync(id, request, ct);
            return result.Status == AdminStatus.Done ? Results.Ok(await service.UserAsync(id, ct)) : ToProblem(result);
        });

        var prefixes = app.MapGroup("/weight-prefixes").WithTags("Весовой товар").RequireAuthorization(ManagePolicy);

        prefixes.MapGet("/", async (AdminService service, CancellationToken ct) => Results.Ok(await service.WeightPrefixesAsync(ct)));

        // Префиксы 20–29, отданные весам. Список заменяется целиком; пустой — весовые все 20–29.
        prefixes.MapPut("/", async (SetWeightPrefixes request, AdminService service, CancellationToken ct) =>
        {
            var result = await service.SetWeightPrefixesAsync(request, ct);
            return result.Status == AdminStatus.Done ? Results.Ok(await service.WeightPrefixesAsync(ct)) : ToProblem(result);
        });
    }

    private static IResult ToProblem(AdminResult result) => result.Status switch
    {
        AdminStatus.NotFound => Results.Problem(statusCode: 404, title: "Не найдено", detail: result.Detail),
        _ => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: result.Detail),
    };
}
