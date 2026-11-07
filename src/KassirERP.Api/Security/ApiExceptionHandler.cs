using KassirERP.Application.Catalog;
using Microsoft.AspNetCore.Diagnostics;

namespace KassirERP.Api.Security;

/// <summary>Ожидаемые исключения → ответ с кодом, а не 500.</summary>
internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        IResult? result = exception switch
        {
            CatalogValidationException e => Results.Problem(statusCode: 400, title: "Неверный запрос", detail: e.Message),
            // Тело не разобралось: в т.ч. неизвестное поле (tenantId) в JSON.
            BadHttpRequestException e => Results.Problem(statusCode: e.StatusCode, title: "Неверный запрос", detail: e.Message),
            UnauthorizedAccessException => Results.Problem(statusCode: 401, title: "Нужен вход"),
            _ => null,
        };

        if (result is null)
        {
            return false;
        }

        await result.ExecuteAsync(context);
        return true;
    }
}
