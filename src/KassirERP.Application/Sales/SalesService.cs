using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Sales;

public sealed class SaleValidationException(string message) : AppValidationException(message);

/// <summary>Продажа (шаг 5): смена, чек, возврат. Только с зарегистрированной кассы.</summary>
public sealed class SalesService(ISalesStore store, ITenantContext tenant)
{
    public const int MaxItems = 500;
    public const int MaxPayments = 10;

    public Task<SaleResult> OpenShiftAsync(NewShift shift, CancellationToken ct)
    {
        if (shift.Id == Guid.Empty)
        {
            throw new SaleValidationException("id смены обязателен: его создаёт касса.");
        }

        RequireMoney(shift.OpeningCash, "Наличные на начало смены");
        return store.OpenShiftAsync(shift, Context(), ct);
    }

    public Task<SaleResult> CloseShiftAsync(Guid shiftId, CloseShift close, CancellationToken ct)
    {
        RequireMoney(close.ClosingCash, "Наличные на конец смены");
        return store.CloseShiftAsync(shiftId, close, Context(), ct);
    }

    public Task<ShiftView?> GetShiftAsync(Guid shiftId, CancellationToken ct) => store.GetShiftAsync(shiftId, ct);

    /// <param name="late">Документ из синхронизации: в закрытую смену принимается как поздний.</param>
    public Task<SaleResult> CreateSaleAsync(NewSale sale, CancellationToken ct, bool late = false)
    {
        if (sale.Id == Guid.Empty || sale.ShiftId == Guid.Empty)
        {
            throw new SaleValidationException("Нужны id чека и id смены: их создаёт касса.");
        }

        if (sale.LocalNumber <= 0)
        {
            throw new SaleValidationException("Номер чека на кассе — положительное число.");
        }

        if (sale.Type is not (SaleTypes.Sale or SaleTypes.Return))
        {
            throw new SaleValidationException("Тип чека: sale или return.");
        }

        var isReturn = sale.Type == SaleTypes.Return;
        if (isReturn != (sale.ReturnOfSaleId is not null))
        {
            throw new SaleValidationException("returnOfSaleId — у возврата обязателен, у продажи его нет.");
        }

        if (sale.Items is null || sale.Items.Count == 0 || sale.Items.Count > MaxItems)
        {
            throw new SaleValidationException($"В чеке от 1 до {MaxItems} строк.");
        }

        foreach (var item in sale.Items)
        {
            if (item.Id == Guid.Empty)
            {
                throw new SaleValidationException("id строки обязателен: его создаёт касса.");
            }

            if (item.Qty <= 0 || item.Qty > 1_000_000m || decimal.Round(item.Qty, 3) != item.Qty)
            {
                throw new SaleValidationException("Количество: больше 0, не больше трёх знаков после запятой.");
            }

            if (isReturn)
            {
                if (item.ReturnOfItemId is null || item.VariantId is not null || item.Price is not null || item.Discount != 0)
                {
                    throw new SaleValidationException("Строка возврата: только returnOfItemId и qty — пара, цена и скидка берутся из исходного чека.");
                }
            }
            else
            {
                if (item.VariantId is null || item.Price is null || item.ReturnOfItemId is not null)
                {
                    throw new SaleValidationException("Строка продажи: нужны variantId и price.");
                }

                RequireMoney(item.Price.Value, "Цена");
                RequireMoney(item.Discount, "Скидка");
                if (item.Discount > SaleMath.Gross(item.Price.Value, item.Qty))
                {
                    throw new SaleValidationException("Скидка больше суммы строки.");
                }
            }

            if (item.ScannedCode is { Length: > 64 })
            {
                throw new SaleValidationException("Отсканированный код: до 64 символов.");
            }
        }

        if (sale.Items.Select(i => i.Id).Distinct().Count() != sale.Items.Count)
        {
            throw new SaleValidationException("id строк в чеке повторяются.");
        }

        if (sale.Payments is null || sale.Payments.Count > MaxPayments)
        {
            throw new SaleValidationException($"Оплат в чеке — не больше {MaxPayments}.");
        }

        foreach (var payment in sale.Payments)
        {
            if (payment.Id == Guid.Empty || !PaymentMethods.All.Contains(payment.Method))
            {
                throw new SaleValidationException("Оплата: нужен id, способ — cash, card, transfer или other.");
            }

            if (payment.Amount <= 0)
            {
                throw new SaleValidationException("Сумма оплаты — больше 0.");
            }

            RequireMoney(payment.Amount, "Сумма оплаты");
        }

        if (sale.Payments.Select(p => p.Id).Distinct().Count() != sale.Payments.Count)
        {
            throw new SaleValidationException("id оплат повторяются.");
        }

        return store.CreateSaleAsync(sale, Context() with { Late = late }, ct);
    }

    public Task<SaleView?> GetSaleAsync(Guid saleId, CancellationToken ct) => store.GetSaleAsync(saleId, ct);

    public Task<DeviceState> GetDeviceStateAsync(CancellationToken ct) => store.GetDeviceStateAsync(Context(), ct);

    /// <summary>Чек пробивает касса: филиал и касса — из токена, полученного по PIN на ней.</summary>
    private SaleContext Context()
    {
        var device = tenant.DeviceId ?? throw new SaleValidationException("Чек пробивается только с кассы: войдите по PIN на зарегистрированной кассе.");
        var storeId = tenant.StoreId ?? throw new SaleValidationException("В токене нет филиала.");
        return new SaleContext(storeId, device, tenant.UserId);
    }

    private static void RequireMoney(decimal value, string field)
    {
        if (value < 0 || value > 999_999_999_999.99m || decimal.Round(value, 2) != value)
        {
            throw new SaleValidationException($"{field}: от 0, не больше двух знаков после запятой.");
        }
    }
}
