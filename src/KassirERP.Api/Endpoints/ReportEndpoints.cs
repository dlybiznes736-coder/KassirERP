using KassirERP.Application.Reports;

namespace KassirERP.Api.Endpoints;

/// <summary>
/// Отчёты (шаг 7). Деньги (продажи и прибыль, расхождения цен, смены, поздние
/// возвраты) — владелец, администратор, менеджер. Склад (остатки, сроки,
/// минус) — они же и кладовщик. Кассир — 403 на всё.
/// </summary>
internal static class ReportEndpoints
{
    public const string FinancePolicy = "reports.finance";
    public const string StockPolicy = "reports.stock";

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/reports").WithTags("Отчёты");

        // Продажи и прибыль по дням и филиалам; без storeId — все филиалы.
        reports.MapGet("/sales", async (DateOnly? from, DateOnly? to, Guid? storeId, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.SalesAsync(from, to, storeId, ct))).RequireAuthorization(FinancePolicy);

        reports.MapGet("/price-mismatches", async (DateOnly? from, DateOnly? to, Guid? storeId, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.PriceMismatchesAsync(from, to, storeId, ct))).RequireAuthorization(FinancePolicy);

        // Смены с разницей наличных; changedOnly=true — только изменённые после закрытия.
        reports.MapGet("/shifts", async (DateOnly? from, DateOnly? to, Guid? storeId, bool? changedOnly, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.ShiftsAsync(from, to, storeId, changedOnly, ct))).RequireAuthorization(FinancePolicy);

        // Поздние возвраты больше проданного — на разбор.
        reports.MapGet("/late-returns", async (DateOnly? from, DateOnly? to, Guid? storeId, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.ReviewAsync(from, to, storeId, ct))).RequireAuthorization(FinancePolicy);

        // Склад — по филиалу (storeId или филиал из токена), страницами.
        reports.MapGet("/stock", async (Guid? storeId, string? after, int? limit, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.StockAsync(storeId, after, limit, ct))).RequireAuthorization(StockPolicy);

        // Что истекает в ближайшие days дней (по умолчанию 7); expired=true — и просроченное за 90 дней.
        reports.MapGet("/expiring", async (Guid? storeId, int? days, bool? expired, string? after, int? limit, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.ExpiringAsync(storeId, days, expired, after, limit, ct))).RequireAuthorization(StockPolicy);

        reports.MapGet("/negative-stock", async (Guid? storeId, string? after, int? limit, ReportService service, CancellationToken ct) =>
            Results.Ok(await service.NegativeStockAsync(storeId, after, limit, ct))).RequireAuthorization(StockPolicy);
    }
}
