using System.Globalization;
using KassirERP.Application.Sales;

namespace KassirERP.Application.Sync;

// --- Отправка: касса → сервер ---------------------------------------------

public sealed record SyncClose(Guid ShiftId, decimal ClosingCash);

/// <summary>
/// Пачка с кассы. Сервер разбирает её в порядке: открытия смен → чеки продажи →
/// чеки возврата → закрытия смен. Каждый документ — в своей транзакции:
/// плохой документ не валит пачку.
/// </summary>
public sealed record SyncPush(IReadOnlyList<NewShift>? Shifts, IReadOnlyList<NewSale>? Sales, IReadOnlyList<SyncClose>? Closes);

public static class SyncItemStatus
{
    /// <summary>Принят сейчас.</summary>
    public const string Accepted = "accepted";

    /// <summary>Уже был принят раньше (повтор пачки) — второй копии нет.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Отклонён окончательно; повтор не поможет — нужен человек. Касса хранит его с причиной.</summary>
    public const string Rejected = "rejected";

    /// <summary>Не принят пока: нет исходного чека или смены, временная ошибка. Касса пришлёт его снова.</summary>
    public const string Retry = "retry";
}

/// <summary>Id — id смены (открытие и закрытие) или чека.</summary>
public sealed record SyncItemResult(Guid Id, string Status, string? Error = null);

public sealed record SyncPushResult(
    IReadOnlyList<SyncItemResult> Shifts, IReadOnlyList<SyncItemResult> Sales, IReadOnlyList<SyncItemResult> Closes,
    int Accepted, int Duplicates, int Rejected, int Retry);

// --- Забор изменений: сервер → касса --------------------------------------

/// <summary>Товар (модель). Deleted — убрать из кассы.</summary>
public sealed record ProductChange(Guid Id, string Name, Guid? CategoryId, bool IsActive, bool Deleted, DateTimeOffset UpdatedAt);

/// <summary>Пара. BasePrice — базовая цена (если в листе филиала цены нет).</summary>
public sealed record VariantChange(
    Guid Id, Guid ProductId, string? Size, string? Color, string? Barcode, string Unit, string? Plu,
    decimal BasePrice, bool TracksExpiry, bool IsActive, bool Deleted, DateTimeOffset UpdatedAt);

/// <summary>Цена пары в листе филиала. SalePrice = null — цену из листа убрали: действует базовая.</summary>
public sealed record PriceChange(Guid VariantId, decimal? SalePrice, DateTimeOffset ChangedAt);

/// <summary>
/// Страница изменений. Next — курсор для следующего запроса (касса хранит его
/// как «последнюю синхронизацию»); HasMore — есть ещё, спрашивать сразу.
/// Для цен: PriceListId — лист филиала сейчас; Reset — лист сменился, касса
/// сбрасывает цены листа и получает их с начала.
/// </summary>
public sealed record SyncChanges<T>(string Entity, Guid? PriceListId, bool Reset, IReadOnlyList<T> Items, string Next, bool HasMore);

/// <summary>
/// Курсор: момент изменения (мкс), id, вид строки (для цен: 0 — снятие, 1 — цена)
/// и — для цен — лист, к которому относятся уже полученные цены (Guid.Empty —
/// листа не было; null — касса начинает с нуля). Для кассы курсор непрозрачен.
/// </summary>
public readonly record struct SyncCursor(DateTimeOffset At, Guid Id, int Kind, Guid? ListId = null)
{
    public static readonly SyncCursor Start = new(DateTimeOffset.UnixEpoch.AddYears(-1000), Guid.Empty, -1);

    private static readonly long MinMicros = (DateTimeOffset.MinValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
    private static readonly long MaxMicros = (DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    public bool IsStart => At == Start.At && Id == Guid.Empty && Kind == -1;

    /// <summary>
    /// Откуда отдавать цены, если сейчас у филиала лист currentList (null — без листа).
    /// Лист сменился — с начала и Reset: касса сбрасывает прежние цены листа.
    /// </summary>
    public (SyncCursor From, bool Reset) ForList(Guid? currentList)
    {
        var list = currentList ?? Guid.Empty;
        return IsStart || ListId != list
            ? (Start with { ListId = list }, true)
            : (this, false);
    }

    public override string ToString()
    {
        var head = string.Create(CultureInfo.InvariantCulture, $"{(At.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10}_{Id:N}_{Kind}");
        return ListId is { } list ? $"{head}_{list:N}" : head;
    }

    public static bool TryParse(string? text, out SyncCursor cursor)
    {
        cursor = Start;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var parts = text.Split('_');
        Guid list = Guid.Empty;
        if (parts.Length is < 3 or > 4
            || !long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var micros)
            || !Guid.TryParseExact(parts[1], "N", out var id)
            || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var kind)
            || (parts.Length == 4 && !Guid.TryParseExact(parts[3], "N", out list))
            || micros < MinMicros || micros > MaxMicros || kind is < -1 or > 1)
        {
            return false;
        }

        cursor = new SyncCursor(DateTimeOffset.UnixEpoch.AddTicks(micros * 10), id, kind, parts.Length == 4 ? list : null);
        return true;
    }
}

/// <summary>Чтение изменений через TenantDb (тенант из токена кассы).</summary>
public interface ISyncStore
{
    Task<(IReadOnlyList<ProductChange> Items, bool HasMore)> ProductsAsync(SyncCursor after, int limit, CancellationToken ct);
    Task<(IReadOnlyList<VariantChange> Items, bool HasMore)> VariantsAsync(SyncCursor after, int limit, CancellationToken ct);

    /// <summary>
    /// Цены листа филиала после курсора. Лист читается в той же транзакции:
    /// если он не тот, что в курсоре, — отдаёт с начала (Reset).
    /// Next — курсор после последней отданной строки.
    /// </summary>
    Task<(Guid? PriceListId, bool Reset, IReadOnlyList<PriceChange> Items, SyncCursor Next, bool HasMore)> PricesAsync(
        Guid storeId, SyncCursor after, int limit, CancellationToken ct);
}
