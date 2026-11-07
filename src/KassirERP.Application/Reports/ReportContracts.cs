namespace KassirERP.Application.Reports;

// Отчёты (шаг 7). Дни — по местному времени филиала (stores.timezone).
// Период — from..to включительно, не длиннее 400 дней. Без storeId —
// по всем филиалам фирмы (для отчётов о продажах, сменах, расхождениях).

/// <summary>Период и филиал отчёта.</summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To, Guid? StoreId);

/// <summary>
/// Продажи и прибыль за день в филиале. Revenue — чеки продажи, Refunds — возвраты,
/// Net = Revenue − Refunds. Cost — себестоимость проданного минус вернувшегося
/// (скопирована в строки чека на момент продажи), Profit = Net − Cost.
/// </summary>
public sealed record SalesDayRow(
    DateOnly Day, Guid StoreId, int Sales, int Returns, decimal Revenue, decimal Refunds, decimal Net, decimal Cost, decimal Profit);

public sealed record SalesReport(ReportPeriod Period, IReadOnlyList<SalesDayRow> Days, SalesDayRow? Total);

/// <summary>Страница отчёта: Next — курсор следующей страницы (null — последняя).</summary>
public sealed record ReportPage<T>(IReadOnlyList<T> Items, string? Next);

public sealed record StockRow(
    Guid VariantId, Guid ProductId, string Name, string? Size, string? Color, string? Barcode, string Unit,
    decimal Qty, decimal CostPrice, decimal SalePrice);

/// <summary>Партия с остатком: Qty — сколько из партии ещё на полке (списание по FIFO по сроку). DaysLeft &lt; 0 — уже просрочена.</summary>
public sealed record ExpiringRow(Guid VariantId, string Name, string? Barcode, string Unit, DateOnly ExpiresOn, int DaysLeft, decimal Qty);

public sealed record NegativeStockRow(Guid VariantId, string Name, string? Barcode, string Unit, decimal Qty);

/// <summary>Строка чека, пробитая не по цене листа. Difference = (ListPrice − Price) × Qty — недополучено (минус — переплата).</summary>
public sealed record PriceMismatchRow(
    Guid SaleId, Guid StoreId, Guid DeviceId, Guid CashierId, int LocalNumber, DateTimeOffset CreatedAtLocal,
    Guid VariantId, string Name, decimal Qty, decimal Price, decimal ListPrice, decimal Difference);

public sealed record PriceMismatchReport(ReportPeriod Period, int Lines, decimal Difference, IReadOnlyList<PriceMismatchRow> Items);

/// <summary>
/// Смена. Difference = ClosingCash − ExpectedCash (минус — недостача). ChangedAfterClose —
/// после закрытия пришли поздние документы (LateDocuments), ожидаемые наличные пересчитаны.
/// </summary>
public sealed record ShiftRow(
    Guid Id, Guid StoreId, Guid DeviceId, Guid CashierId, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    decimal OpeningCash, decimal? ClosingCash, decimal? ExpectedCash, decimal? Difference, bool ChangedAfterClose, int LateDocuments);

/// <summary>Поздний возврат на разбор: больше проданного, принят в закрытую смену.</summary>
public sealed record ReviewRow(
    Guid SaleId, Guid StoreId, Guid ShiftId, Guid DeviceId, Guid CashierId, int LocalNumber, DateTimeOffset CreatedAtLocal,
    Guid ReturnOfSaleId, decimal Total, string ReviewReason);

public interface IReportStore
{
    Task<IReadOnlyList<SalesDayRow>> SalesAsync(ReportPeriod period, CancellationToken ct);
    Task<ReportPage<StockRow>> StockAsync(Guid storeId, Guid? after, int limit, CancellationToken ct);
    Task<ReportPage<ExpiringRow>> ExpiringAsync(Guid storeId, int days, bool includeExpired, (DateOnly ExpiresOn, Guid VariantId)? after, int limit, CancellationToken ct);
    Task<ReportPage<NegativeStockRow>> NegativeStockAsync(Guid storeId, (decimal Qty, Guid VariantId)? after, int limit, CancellationToken ct);
    Task<IReadOnlyList<PriceMismatchRow>> PriceMismatchesAsync(ReportPeriod period, int limit, CancellationToken ct);
    Task<IReadOnlyList<ShiftRow>> ShiftsAsync(ReportPeriod period, bool changedOnly, CancellationToken ct);
    Task<IReadOnlyList<ReviewRow>> ReviewAsync(ReportPeriod period, CancellationToken ct);

    /// <summary>Филиал есть в фирме (под RLS) — чтобы чужой филиал давал 404, а не пустой отчёт.</summary>
    Task<bool> StoreExistsAsync(Guid storeId, CancellationToken ct);
}
