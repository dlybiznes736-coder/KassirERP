namespace KassirERP.Application.Sales;

public static class SaleTypes
{
    public const string Sale = "sale";
    public const string Return = "return";
}

public static class PaymentMethods
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string Transfer = "transfer";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Cash, Card, Transfer, Other };
}

// --- Смена ---------------------------------------------------------------

/// <summary>id смены создаёт касса (принцип 1): повтор открытия той же смены — не дубль.</summary>
public sealed record NewShift(Guid Id, decimal OpeningCash);

public sealed record CloseShift(decimal ClosingCash);

/// <summary>
/// ClosingCash — посчитал кассир, ExpectedCash — посчитала система. ChangedAfterClose —
/// после закрытия пришёл поздний документ: ожидаемые наличные пересчитаны (журнал shift_changes).
/// </summary>
public sealed record ShiftView(
    Guid Id, Guid StoreId, Guid DeviceId, Guid CashierId, string Status,
    DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, decimal OpeningCash, decimal? ClosingCash, decimal? ExpectedCash,
    bool ChangedAfterClose = false);

// --- Чек -----------------------------------------------------------------

/// <summary>
/// Строка чека. Продажа: VariantId, Qty, Price (цена, которую видел покупатель, —
/// касса могла пробить её без связи), Discount, ScannedCode (весовой код).
/// Возврат: ReturnOfItemId и Qty; пара, цена и скидка берутся из исходной строки.
/// </summary>
public sealed record NewSaleItem(
    Guid Id, Guid? VariantId, decimal Qty, decimal? Price = null, decimal Discount = 0,
    string? ScannedCode = null, Guid? ReturnOfItemId = null);

public sealed record NewPayment(Guid Id, string Method, decimal Amount);

/// <summary>
/// Чек с кассы. id и номер чека (LocalNumber, по порядку на кассе) создаёт касса.
/// Филиал, касса и кассир — из токена; в запросе их нет.
/// </summary>
public sealed record NewSale(
    Guid Id, Guid ShiftId, int LocalNumber, DateTimeOffset CreatedAtLocal,
    IReadOnlyList<NewSaleItem> Items, IReadOnlyList<NewPayment> Payments,
    string Type = SaleTypes.Sale, Guid? ReturnOfSaleId = null, Guid? CustomerId = null);

/// <summary>
/// ListPrice — цена листа филиала (или базовая) на момент приёма чека;
/// PriceMismatch — касса пробила другую цену. Чек при этом принят.
/// У строк возврата ListPrice = null: цена берётся из исходного чека.
/// </summary>
public sealed record SaleItemView(
    Guid Id, Guid VariantId, string Unit, decimal Qty, decimal Price, decimal Discount, decimal Total,
    string? ScannedCode, Guid? ReturnOfItemId, decimal? ListPrice, bool PriceMismatch);

public sealed record PaymentView(Guid Id, string Method, decimal Amount);

/// <summary>
/// AfterClose — поздний документ: принят в уже закрытую смену. ReviewReason — поздний
/// возврат больше проданного: принят, но требует разбора (null — не требует).
/// </summary>
public sealed record SaleView(
    Guid Id, string Type, Guid StoreId, Guid ShiftId, Guid DeviceId, Guid CashierId, int LocalNumber,
    Guid? ReturnOfSaleId, Guid? CustomerId, decimal Subtotal, decimal DiscountTotal, decimal Total,
    DateTimeOffset CreatedAtLocal, IReadOnlyList<SaleItemView> Items, IReadOnlyList<PaymentView> Payments,
    bool AfterClose = false, string? ReviewReason = null);

public enum SaleStatus
{
    Done,
    /// <summary>Повтор того же чека: в базе уже он же, ничего не создано.</summary>
    AlreadyDone,
    /// <summary>Тот же id с другим содержимым, номер чека на кассе занят или id занят.</summary>
    Conflict,
    NotFound,
    /// <summary>Смена закрыта.</summary>
    ShiftClosed,
    /// <summary>Неверная ссылка: смена не этой кассы, пара или покупатель не найдены, возврат не на продажу.</summary>
    Invalid,
    /// <summary>Вернуть больше, чем продано (с учётом прежних возвратов).</summary>
    ReturnExceedsSold,
    /// <summary>Исходного чека возврата на сервере нет (ещё не пришёл). Онлайн — 400, при синхронизации — повторить позже.</summary>
    OriginalMissing,
    /// <summary>Смены чека на сервере нет (ещё не пришла). Онлайн — 400, при синхронизации — повторить позже.</summary>
    ShiftMissing,
}

/// <summary>
/// Состояние кассы на сервере: открытая смена и последний номер чека. Нужно кассе
/// после переустановки или сбоя, чтобы продолжить нумерацию и не открыть вторую смену.
/// </summary>
public sealed record DeviceState(Guid DeviceId, Guid StoreId, int LastLocalNumber, ShiftView? OpenShift);

public sealed record SaleResult(SaleStatus Status, string? Detail = null, SaleView? Sale = null, ShiftView? Shift = null);

/// <summary>
/// Кто пробивает чек — только из токена кассы. Late — документ пришёл синхронизацией
/// (сделан без связи): если его смена уже закрыта, он принимается как поздний.
/// </summary>
public sealed record SaleContext(Guid StoreId, Guid DeviceId, Guid UserId, bool Late = false);

/// <summary>Смены и чеки. Всё через TenantDb, в тенанте из токена.</summary>
public interface ISalesStore
{
    Task<SaleResult> OpenShiftAsync(NewShift shift, SaleContext context, CancellationToken ct);
    Task<SaleResult> CloseShiftAsync(Guid shiftId, CloseShift close, SaleContext context, CancellationToken ct);
    Task<ShiftView?> GetShiftAsync(Guid shiftId, CancellationToken ct);
    Task<SaleResult> CreateSaleAsync(NewSale sale, SaleContext context, CancellationToken ct);
    Task<SaleView?> GetSaleAsync(Guid saleId, CancellationToken ct);
    Task<DeviceState> GetDeviceStateAsync(SaleContext context, CancellationToken ct);
}

/// <summary>Деньги чека — в одном месте. Округление до тыйына, от нуля.</summary>
public static class SaleMath
{
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Сумма строки до скидки: цена × количество (кг — с граммами).</summary>
    public static decimal Gross(decimal price, decimal qty) => Round(price * qty);

    /// <summary>Скидка на часть строки при возврате — пропорционально количеству.</summary>
    public static decimal ProportionalDiscount(decimal discount, decimal qty, decimal originalQty) =>
        Round(discount * qty / originalQty);
}
