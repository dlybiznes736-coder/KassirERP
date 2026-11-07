using System.Globalization;
using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Reports;

public sealed class ReportValidationException(string message) : AppValidationException(message);

public sealed class ReportNotFoundException(string message) : Exception(message);

/// <summary>Отчёты (шаг 7). Только чтение; тенант — из токена.</summary>
public sealed class ReportService(IReportStore store, ITenantContext tenant)
{
    public const int MaxDays = 400;
    public const int DefaultPage = 500;
    public const int MaxPage = 1000;
    public const int MaxMismatchLines = 5000;

    public async Task<SalesReport> SalesAsync(DateOnly? from, DateOnly? to, Guid? storeId, CancellationToken ct)
    {
        var period = await PeriodAsync(from, to, storeId, ct);
        var days = await store.SalesAsync(period, ct);
        SalesDayRow? total = days.Count == 0
            ? null
            : new SalesDayRow(period.To, period.StoreId ?? Guid.Empty, days.Sum(d => d.Sales), days.Sum(d => d.Returns),
                days.Sum(d => d.Revenue), days.Sum(d => d.Refunds), days.Sum(d => d.Net), days.Sum(d => d.Cost), days.Sum(d => d.Profit));
        return new SalesReport(period, days, total);
    }

    public async Task<ReportPage<StockRow>> StockAsync(Guid? storeId, string? after, int? limit, CancellationToken ct)
    {
        Guid? cursor = null;
        if (!string.IsNullOrEmpty(after))
        {
            cursor = Guid.TryParseExact(after, "N", out var id) ? id : throw new ReportValidationException("Неверный курсор страницы.");
        }

        return await store.StockAsync(await StoreAsync(storeId, ct), cursor, Page(limit), ct);
    }

    public async Task<ReportPage<ExpiringRow>> ExpiringAsync(Guid? storeId, int? days, bool? expired, string? after, int? limit, CancellationToken ct)
    {
        var window = days ?? 7;
        if (window is < 0 or > 365)
        {
            throw new ReportValidationException("days: от 0 до 365.");
        }

        (DateOnly, Guid)? cursor = null;
        if (!string.IsNullOrEmpty(after))
        {
            var parts = after.Split('_');
            cursor = parts.Length == 2
                     && DateOnly.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                     && Guid.TryParseExact(parts[1], "N", out var id)
                ? (date, id)
                : throw new ReportValidationException("Неверный курсор страницы.");
        }

        return await store.ExpiringAsync(await StoreAsync(storeId, ct), window, expired ?? false, cursor, Page(limit), ct);
    }

    public async Task<ReportPage<NegativeStockRow>> NegativeStockAsync(Guid? storeId, string? after, int? limit, CancellationToken ct)
    {
        (decimal, Guid)? cursor = null;
        if (!string.IsNullOrEmpty(after))
        {
            var parts = after.Split('_');
            cursor = parts.Length == 2
                     && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
                     && Guid.TryParseExact(parts[1], "N", out var id)
                ? (qty, id)
                : throw new ReportValidationException("Неверный курсор страницы.");
        }

        return await store.NegativeStockAsync(await StoreAsync(storeId, ct), cursor, Page(limit), ct);
    }

    public async Task<PriceMismatchReport> PriceMismatchesAsync(DateOnly? from, DateOnly? to, Guid? storeId, CancellationToken ct)
    {
        var period = await PeriodAsync(from, to, storeId, ct);
        var rows = await store.PriceMismatchesAsync(period, MaxMismatchLines, ct);
        return new PriceMismatchReport(period, rows.Count, rows.Sum(r => r.Difference), rows);
    }

    public async Task<IReadOnlyList<ShiftRow>> ShiftsAsync(DateOnly? from, DateOnly? to, Guid? storeId, bool? changedOnly, CancellationToken ct) =>
        await store.ShiftsAsync(await PeriodAsync(from, to, storeId, ct), changedOnly ?? false, ct);

    public async Task<IReadOnlyList<ReviewRow>> ReviewAsync(DateOnly? from, DateOnly? to, Guid? storeId, CancellationToken ct) =>
        await store.ReviewAsync(await PeriodAsync(from, to, storeId, ct), ct);

    public static string Cursor(DateOnly expiresOn, Guid variantId) =>
        expiresOn.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_" + variantId.ToString("N");

    public static string Cursor(decimal qty, Guid variantId) =>
        qty.ToString(CultureInfo.InvariantCulture) + "_" + variantId.ToString("N");

    // -----------------------------------------------------------------------

    private async Task<ReportPeriod> PeriodAsync(DateOnly? from, DateOnly? to, Guid? storeId, CancellationToken ct)
    {
        if (from is null || to is null)
        {
            throw new ReportValidationException("Нужен период: from и to (ГГГГ-ММ-ДД, оба дня включительно).");
        }

        if (to < from || to.Value.DayNumber - from.Value.DayNumber + 1 > MaxDays)
        {
            throw new ReportValidationException($"Период: to не раньше from, не длиннее {MaxDays} дней.");
        }

        if (storeId is { } id && !await store.StoreExistsAsync(id, ct))
        {
            throw new ReportNotFoundException("Филиал не найден.");
        }

        return new ReportPeriod(from.Value, to.Value, storeId);
    }

    /// <summary>Отчёты по складу — по одному филиалу: из запроса или из токена.</summary>
    private async Task<Guid> StoreAsync(Guid? storeId, CancellationToken ct)
    {
        var id = storeId ?? tenant.StoreId ?? throw new ReportValidationException("Нужен storeId: отчёт по складу строится по филиалу.");
        return await store.StoreExistsAsync(id, ct) ? id : throw new ReportNotFoundException("Филиал не найден.");
    }

    private static int Page(int? limit) => limit switch
    {
        null => DefaultPage,
        < 1 or > MaxPage => throw new ReportValidationException($"limit: от 1 до {MaxPage}."),
        _ => limit.Value,
    };
}
