namespace KassirERP.Api.Security;

/// <summary>
/// tenant_id приходит только в подписанном токене. Запрос, который пытается
/// передать его параметром URL или заголовком, — это ошибка клиента или атака;
/// отвечаем 400, а не молча игнорируем. (Поле в теле JSON отклоняет
/// сериализатор: UnmappedMemberHandling.Disallow в Program.cs.)
/// </summary>
internal sealed class TenantInputGuard(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var offending = request.Query.Keys.FirstOrDefault(IsTenantKey) ?? request.Headers.Keys.FirstOrDefault(IsTenantKey);
        if (offending is null)
        {
            return next(context);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "tenant_id нельзя передавать в запросе",
            detail: $"«{offending}»: фирма определяется только по токену.").ExecuteAsync(context);
    }

    // tenant, tenant_id, tenantId, Tenant-Id, X-Tenant-Id, x_tenant ...
    private static bool IsTenantKey(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is "tenant" or "tenantid" or "xtenant" or "xtenantid";
    }
}
