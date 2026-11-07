using KassirERP.Application.Abstractions;
using KassirERP.Application.Sales;
using Microsoft.Extensions.Logging;

namespace KassirERP.Application.Sync;

public sealed class SyncValidationException(string message) : AppValidationException(message);

/// <summary>
/// Синхронизация кассы (шаг 6). Отправка: каждый документ пачки — через те же
/// SalesService, что и онлайн-чек, в своей транзакции; результат — по каждому.
/// Забор: изменения товаров, пар и цен листа филиала после курсора.
/// </summary>
public sealed class SyncService(SalesService sales, ISyncStore store, ITenantContext tenant, ILogger<SyncService> logger)
{
    public const int MaxSales = 1000;
    public const int MaxShifts = 50;
    public const int DefaultPage = 1000;
    public const int MaxPage = 5000;

    public async Task<SyncPushResult> PushAsync(SyncPush push, CancellationToken ct)
    {
        if (tenant.DeviceId is null)
        {
            throw new SyncValidationException("Синхронизация — только с кассы: войдите по PIN на зарегистрированной кассе.");
        }

        var shifts = push.Shifts ?? [];
        var saleDocs = push.Sales ?? [];
        var closes = push.Closes ?? [];
        if (saleDocs.Count > MaxSales || shifts.Count > MaxShifts || closes.Count > MaxShifts)
        {
            throw new SyncValidationException($"В пачке до {MaxSales} чеков и до {MaxShifts} открытий и закрытий смен.");
        }

        // 1. Открытия смен: чеки ссылаются на них.
        var shiftResults = new List<SyncItemResult>(shifts.Count);
        foreach (var shift in shifts)
        {
            shiftResults.Add(await RunAsync(shift?.Id ?? Guid.Empty,
                () => sales.OpenShiftAsync(shift ?? throw new SaleValidationException("Пустая смена."), ct), ct));
        }

        var rejectedShifts = shiftResults.Where(r => r.Status == SyncItemStatus.Rejected).Select(r => r.Id).ToHashSet();

        // 2. Чеки: сначала продажи, потом возвраты — возврат, пришедший в пачке
        //    раньше своего чека, всё равно найдёт его. Внутри вида — порядок кассы.
        var ordered = saleDocs.Select((sale, index) => (sale, index))
            .OrderBy(x => x.sale?.Type == SaleTypes.Return ? 1 : 0).ThenBy(x => x.index).ToList();
        var saleResults = new SyncItemResult[saleDocs.Count];
        foreach (var (sale, index) in ordered)
        {
            saleResults[index] = sale is not null && rejectedShifts.Contains(sale.ShiftId)
                ? new SyncItemResult(sale.Id, SyncItemStatus.Rejected, "Смена чека отклонена в этой же пачке.")
                : await RunAsync(sale?.Id ?? Guid.Empty,
                    () => sales.CreateSaleAsync(sale ?? throw new SaleValidationException("Пустой чек."), ct), ct);
        }

        // 3. Закрытия — последними. Если чек этой смены не принят из-за временной
        //    ошибки, закрытие откладывается: закрытая смена новых чеков не примет.
        var transient = saleDocs.Zip(saleResults)
            .Where(x => x.First is not null && x.Second.Status == SyncItemStatus.Retry && x.Second.Error == TemporaryError)
            .Select(x => x.First.ShiftId).ToHashSet();
        var closeResults = new List<SyncItemResult>(closes.Count);
        foreach (var close in closes)
        {
            if (close is null)
            {
                closeResults.Add(new SyncItemResult(Guid.Empty, SyncItemStatus.Rejected, "Пустое закрытие смены."));
            }
            else if (transient.Contains(close.ShiftId))
            {
                closeResults.Add(new SyncItemResult(close.ShiftId, SyncItemStatus.Retry, "Чек этой смены не принят из-за временной ошибки — закрытие позже."));
            }
            else if (rejectedShifts.Contains(close.ShiftId))
            {
                closeResults.Add(new SyncItemResult(close.ShiftId, SyncItemStatus.Rejected, "Смена отклонена в этой же пачке."));
            }
            else
            {
                closeResults.Add(await RunAsync(close.ShiftId,
                    () => sales.CloseShiftAsync(close.ShiftId, new CloseShift(close.ClosingCash), ct), ct, closing: true));
            }
        }

        var all = shiftResults.Concat(saleResults).Concat(closeResults).ToList();
        return new SyncPushResult(shiftResults, saleResults, closeResults,
            all.Count(r => r.Status == SyncItemStatus.Accepted), all.Count(r => r.Status == SyncItemStatus.Duplicate),
            all.Count(r => r.Status == SyncItemStatus.Rejected), all.Count(r => r.Status == SyncItemStatus.Retry));
    }

    public async Task<SyncChanges<ProductChange>> ProductsAsync(string? after, int? limit, CancellationToken ct)
    {
        var cursor = Cursor(after);
        var (items, hasMore) = await store.ProductsAsync(cursor, Page(limit), ct);
        var next = items.Count == 0 ? cursor : new SyncCursor(items[^1].UpdatedAt, items[^1].Id, 0);
        return new SyncChanges<ProductChange>("products", null, false, items, next.ToString(), hasMore);
    }

    public async Task<SyncChanges<VariantChange>> VariantsAsync(string? after, int? limit, CancellationToken ct)
    {
        var cursor = Cursor(after);
        var (items, hasMore) = await store.VariantsAsync(cursor, Page(limit), ct);
        var next = items.Count == 0 ? cursor : new SyncCursor(items[^1].UpdatedAt, items[^1].Id, 0);
        return new SyncChanges<VariantChange>("variants", null, false, items, next.ToString(), hasMore);
    }

    public async Task<SyncChanges<PriceChange>> PricesAsync(string? after, int? limit, CancellationToken ct)
    {
        var storeId = tenant.StoreId ?? throw new SyncValidationException("В токене нет филиала.");
        var (listId, reset, items, next, hasMore) = await store.PricesAsync(storeId, Cursor(after), Page(limit), ct);
        return new SyncChanges<PriceChange>("prices", listId, reset, items, next.ToString(), hasMore);
    }

    // -----------------------------------------------------------------------

    private const string TemporaryError = "Временная ошибка сервера — пришлите документ ещё раз.";

    /// <summary>Один документ пачки: свой результат, исключение не выходит наружу.</summary>
    private async Task<SyncItemResult> RunAsync(Guid id, Func<Task<SaleResult>> work, CancellationToken ct, bool closing = false)
    {
        try
        {
            var result = await work();
            return result.Status switch
            {
                SaleStatus.Done => new SyncItemResult(id, SyncItemStatus.Accepted),
                SaleStatus.AlreadyDone => new SyncItemResult(id, SyncItemStatus.Duplicate),
                // Исходный чек возврата или смена ещё не пришли (с другой кассы, следующей пачкой).
                SaleStatus.OriginalMissing or SaleStatus.ShiftMissing => new SyncItemResult(id, SyncItemStatus.Retry, result.Detail),
                SaleStatus.NotFound when closing => new SyncItemResult(id, SyncItemStatus.Retry, result.Detail),
                _ => new SyncItemResult(id, SyncItemStatus.Rejected, result.Detail),
            };
        }
        catch (AppValidationException ex)
        {
            return new SyncItemResult(id, SyncItemStatus.Rejected, ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Синхронизация: документ {Id} не принят из-за ошибки сервера", id);
            return new SyncItemResult(id, SyncItemStatus.Retry, TemporaryError);
        }
    }

    private static SyncCursor Cursor(string? after) =>
        SyncCursor.TryParse(after, out var cursor) ? cursor : throw new SyncValidationException("Неверный курсор синхронизации.");

    private static int Page(int? limit) => limit switch
    {
        null => DefaultPage,
        < 1 or > MaxPage => throw new SyncValidationException($"limit: от 1 до {MaxPage}."),
        _ => limit.Value,
    };
}
