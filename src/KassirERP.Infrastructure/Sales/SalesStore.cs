using KassirERP.Application.Catalog;
using KassirERP.Application.Sales;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Sales;

/// <summary>
/// Смены и чеки через TenantDb. Строки чека — последний оператор перед
/// COMMIT (правило ТЗ: триггер блокирует строку stock до конца транзакции,
/// чем позже он срабатывает, тем короче блокировка).
/// </summary>
internal sealed class SalesStore(TenantDb db) : ISalesStore
{
    private const string ShiftSql =
        """
        SELECT id, store_id, device_id, cashier_id, status, opened_at, closed_at, opening_cash, closing_cash, expected_cash
        FROM cash_shifts WHERE id = $1
        """;

    private const string SaleSql =
        """
        SELECT id, type, store_id, shift_id, device_id, cashier_id, local_number, return_of_sale_id, customer_id,
               subtotal, discount_total, total, created_at_local
        FROM sales WHERE id = $1
        """;

    private const string SaleItemsSql =
        """
        SELECT id, variant_id, unit, qty, price, discount, total, scanned_code, return_of_item_id
        FROM sale_items WHERE sale_id = $1 ORDER BY id
        """;

    private const string PaymentsSql = "SELECT id, method, amount FROM payments WHERE sale_id = $1 ORDER BY id";

    // --- Смена -------------------------------------------------------------

    public Task<SaleResult> OpenShiftAsync(NewShift shift, SaleContext context, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            try
            {
                var inserted = await ExecAsync(connection, tx,
                    """
                    INSERT INTO cash_shifts (id, tenant_id, store_id, device_id, cashier_id, status, opened_at, opening_cash)
                    VALUES ($1, current_tenant_id(), $2, $3, $4, 'open', now(), $5)
                    ON CONFLICT (id) DO NOTHING
                    """, ct, shift.Id, context.StoreId, context.DeviceId, context.UserId, shift.OpeningCash);

                var stored = await ReadShiftAsync(connection, tx, shift.Id, ct);
                if (inserted == 1)
                {
                    return new SaleResult(SaleStatus.Done, Shift: stored);
                }

                // Повтор открытия той же смены этой кассой — не ошибка.
                return stored is { Status: "open" } && stored.DeviceId == context.DeviceId && stored.OpeningCash == shift.OpeningCash
                    ? new SaleResult(SaleStatus.AlreadyDone, Shift: stored)
                    : new SaleResult(SaleStatus.Conflict, "Смена с таким id уже есть, и она другая.");
            }
            catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_shifts_open_per_device" })
            {
                return new SaleResult(SaleStatus.Conflict, "На этой кассе уже открыта другая смена — сначала закройте её.");
            }
        }, ct);

    public Task<SaleResult> CloseShiftAsync(Guid shiftId, CloseShift close, SaleContext context, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var shift = await ReadShiftAsync(connection, tx, shiftId, ct, forUpdate: true);
            if (shift is null)
            {
                return new SaleResult(SaleStatus.NotFound, "Смена не найдена.");
            }

            if (shift.DeviceId != context.DeviceId)
            {
                return new SaleResult(SaleStatus.Invalid, "Смену закрывает касса, на которой она открыта.");
            }

            if (shift.Status == "closed")
            {
                return new SaleResult(SaleStatus.AlreadyDone, Shift: shift);
            }

            // Ожидаемые наличные: на начало + наличные продаж − наличные возвратов.
            await ExecAsync(connection, tx,
                """
                UPDATE cash_shifts c
                   SET status = 'closed', closed_at = now(), closing_cash = $2,
                       expected_cash = c.opening_cash + coalesce((
                         SELECT sum(CASE WHEN s.type = 'sale' THEN p.amount ELSE -p.amount END)
                         FROM sales s JOIN payments p ON p.sale_id = s.id
                         WHERE s.shift_id = c.id AND p.method = 'cash'), 0)
                 WHERE c.id = $1
                """, ct, shiftId, close.ClosingCash);
            return new SaleResult(SaleStatus.Done, Shift: await ReadShiftAsync(connection, tx, shiftId, ct));
        }, ct);

    public Task<ShiftView?> GetShiftAsync(Guid shiftId, CancellationToken ct) =>
        db.ReadAsync([Batch(ShiftSql, shiftId)], async reader => await reader.ReadAsync(ct) ? ReadShift(reader) : null, ct);

    // --- Чек ---------------------------------------------------------------

    public Task<SaleResult> CreateSaleAsync(NewSale sale, SaleContext context, CancellationToken ct) =>
        db.WriteAsync(async (connection, tx) =>
        {
            var createdAt = TruncateToMicroseconds(sale.CreatedAtLocal);

            // Повтор отправки: тот же чек — 200 без изменений, другой с тем же id — конфликт.
            if (await ReadSaleAsync(connection, tx, sale.Id, ct) is { } existing)
            {
                return Replay(existing, sale, createdAt);
            }

            var shift = await ReadShiftAsync(connection, tx, sale.ShiftId, ct, forShare: true);
            if (shift is null || shift.DeviceId != context.DeviceId || shift.StoreId != context.StoreId)
            {
                return new SaleResult(SaleStatus.Invalid, "Смена не найдена или открыта на другой кассе.");
            }

            if (shift.Status != "open")
            {
                return new SaleResult(SaleStatus.ShiftClosed, "Смена закрыта: откройте новую.");
            }

            if (sale.CustomerId is { } customer && !await ExistsAsync(connection, tx,
                    "SELECT 1 FROM customers WHERE id = $1 AND deleted_at IS NULL", customer, ct))
            {
                return new SaleResult(SaleStatus.Invalid, "Покупатель не найден.");
            }

            var built = sale.Type == SaleTypes.Return
                ? await BuildReturnLinesAsync(connection, tx, sale, ct)
                : await BuildSaleLinesAsync(connection, tx, sale, ct);
            if (built.Error is { } error)
            {
                return error;
            }

            var lines = built.Lines!;
            var subtotal = lines.Sum(l => SaleMath.Gross(l.Price, l.Qty));
            var discountTotal = lines.Sum(l => l.Discount);
            var total = subtotal - discountTotal;
            var paid = sale.Payments.Sum(p => p.Amount);
            if (paid != total)
            {
                return new SaleResult(SaleStatus.Invalid, $"Сумма оплат {paid:0.00} не равна сумме чека {total:0.00}.");
            }

            try
            {
                var inserted = await ExecAsync(connection, tx,
                    """
                    INSERT INTO sales (id, tenant_id, store_id, shift_id, cashier_id, customer_id, device_id, local_number,
                                       type, return_of_sale_id, subtotal, discount_total, total, created_at_local)
                    VALUES ($1, current_tenant_id(), $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                    ON CONFLICT (id) DO NOTHING
                    """, ct,
                    sale.Id, context.StoreId, sale.ShiftId, context.UserId, (object?)sale.CustomerId ?? DBNull.Value, context.DeviceId,
                    sale.LocalNumber, sale.Type, (object?)sale.ReturnOfSaleId ?? DBNull.Value, subtotal, discountTotal, total, createdAt);
                if (inserted == 0)
                {
                    // Тот же чек только что провела параллельная отправка.
                    return Replay((await ReadSaleAsync(connection, tx, sale.Id, ct))!, sale, createdAt);
                }

                await InsertPaymentsAsync(connection, tx, sale, ct);
                await InsertItemsAsync(connection, tx, sale.Id, lines, ct); // последним — перед COMMIT
            }
            catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_sales_device_number" })
            {
                return new SaleResult(SaleStatus.Conflict, $"Номер чека {sale.LocalNumber} на этой кассе уже занят другим чеком.");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return new SaleResult(SaleStatus.Conflict, "id строки или оплаты уже занят.");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.MessageText.Contains("больше проданного", StringComparison.Ordinal))
            {
                return new SaleResult(SaleStatus.ReturnExceedsSold, ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
            {
                return new SaleResult(SaleStatus.Invalid, ex.MessageText);
            }

            return new SaleResult(SaleStatus.Done, Sale: await ReadSaleAsync(connection, tx, sale.Id, ct));
        }, ct);

    public Task<SaleView?> GetSaleAsync(Guid saleId, CancellationToken ct) =>
        db.ReadAsync([Batch(SaleSql, saleId), Batch(SaleItemsSql, saleId), Batch(PaymentsSql, saleId)],
            reader => ReadSaleDocumentAsync(reader, ct), ct);

    // -----------------------------------------------------------------------

    private sealed record Line(Guid Id, Guid VariantId, string Unit, decimal Qty, decimal Price, decimal Discount,
        decimal CostPrice, string? ScannedCode, Guid? ReturnOfItemId);

    private sealed record Built(List<Line>? Lines, SaleResult? Error);

    /// <summary>Продажа: цена и скидка — с кассы; единица и себестоимость — из пары (копируются в чек).</summary>
    private static async Task<Built> BuildSaleLinesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, NewSale sale, CancellationToken ct)
    {
        var variants = new Dictionary<Guid, (string Unit, decimal Cost)>();
        await using (var command = new NpgsqlCommand(
            "SELECT id, unit, cost_price FROM product_variants WHERE id = ANY($1) AND deleted_at IS NULL", connection, tx))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = sale.Items.Select(i => i.VariantId!.Value).Distinct().ToArray() });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                variants[reader.GetGuid(0)] = (reader.GetString(1), reader.GetDecimal(2));
            }
        }

        var lines = new List<Line>(sale.Items.Count);
        foreach (var item in sale.Items)
        {
            if (!variants.TryGetValue(item.VariantId!.Value, out var variant))
            {
                return new Built(null, new SaleResult(SaleStatus.Invalid, $"Пара {item.VariantId} не найдена."));
            }

            if (variant.Unit == Units.Piece && item.Qty != decimal.Truncate(item.Qty))
            {
                return new Built(null, new SaleResult(SaleStatus.Invalid, "Штучный товар продаётся целым числом."));
            }

            lines.Add(new Line(item.Id, item.VariantId.Value, variant.Unit, item.Qty, item.Price!.Value, item.Discount,
                variant.Cost, item.ScannedCode, null));
        }

        return new Built(lines, null);
    }

    /// <summary>
    /// Возврат: строки исходного чека. Пара, цена и себестоимость — оттуда же, скидка —
    /// пропорционально. Вернуть можно не больше проданного с учётом прежних возвратов.
    /// Возвраты одного чека идут строго по очереди (advisory lock): иначе два
    /// одновременных возврата вместе могли бы превысить проданное.
    /// </summary>
    private static async Task<Built> BuildReturnLinesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, NewSale sale, CancellationToken ct)
    {
        var originalId = sale.ReturnOfSaleId!.Value;
        await ExecAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", ct, originalId);

        await using (var type = new NpgsqlCommand("SELECT type FROM sales WHERE id = $1", connection, tx))
        {
            type.Parameters.Add(new NpgsqlParameter { Value = originalId });
            if (await type.ExecuteScalarAsync(ct) is not string originalType)
            {
                return new Built(null, new SaleResult(SaleStatus.Invalid, "Исходный чек не найден."));
            }

            if (originalType != SaleTypes.Sale)
            {
                return new Built(null, new SaleResult(SaleStatus.Invalid, "Возврат оформляется на чек продажи, а не на возврат."));
            }
        }

        var originals = new Dictionary<Guid, (Guid Variant, string Unit, decimal Qty, decimal Price, decimal Discount, decimal Cost, decimal Returned)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT i.id, i.variant_id, i.unit, i.qty, i.price, i.discount, i.cost_price,
                   coalesce((SELECT sum(r.qty) FROM sale_items r WHERE r.return_of_item_id = i.id), 0)
            FROM sale_items i WHERE i.sale_id = $1
            """, connection, tx))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = originalId });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                originals[reader.GetGuid(0)] = (reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4),
                    reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7));
            }
        }

        foreach (var group in sale.Items.GroupBy(i => i.ReturnOfItemId!.Value))
        {
            if (!originals.TryGetValue(group.Key, out var original))
            {
                return new Built(null, new SaleResult(SaleStatus.Invalid, $"Строки {group.Key} нет в исходном чеке."));
            }

            var requested = group.Sum(i => i.Qty);
            if (original.Returned + requested > original.Qty)
            {
                return new Built(null, new SaleResult(SaleStatus.ReturnExceedsSold,
                    $"Возврат больше проданного: продано {original.Qty:0.###}, уже возвращено {original.Returned:0.###}, " +
                    $"сейчас {requested:0.###}."));
            }

            if (original.Unit == Units.Piece && group.Any(i => i.Qty != decimal.Truncate(i.Qty)))
            {
                return new Built(null, new SaleResult(SaleStatus.Invalid, "Штучный товар возвращается целым числом."));
            }
        }

        var lines = sale.Items.Select(i =>
        {
            var o = originals[i.ReturnOfItemId!.Value];
            return new Line(i.Id, o.Variant, o.Unit, i.Qty, o.Price, SaleMath.ProportionalDiscount(o.Discount, i.Qty, o.Qty),
                o.Cost, i.ScannedCode, i.ReturnOfItemId);
        }).ToList();
        return new Built(lines, null);
    }

    private static async Task InsertPaymentsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, NewSale sale, CancellationToken ct)
    {
        if (sale.Payments.Count == 0)
        {
            return;
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO payments (id, tenant_id, sale_id, method, amount)
            SELECT u.id, current_tenant_id(), $1, u.method, u.amount
            FROM unnest($2::uuid[], $3::text[], $4::numeric[]) AS u(id, method, amount)
            """, connection, tx);
        insert.Parameters.Add(new NpgsqlParameter { Value = sale.Id });
        insert.Parameters.Add(new NpgsqlParameter { Value = sale.Payments.Select(p => p.Id).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = sale.Payments.Select(p => p.Method).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = sale.Payments.Select(p => p.Amount).ToArray() });
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid saleId, List<Line> lines, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, return_of_item_id, qty, price, cost_price,
                                    discount, total, unit, scanned_code)
            SELECT u.id, current_tenant_id(), $1, u.variant_id, u.return_of, u.qty, u.price, u.cost, u.discount,
                   round(u.price * u.qty, 2) - u.discount, u.unit, u.code
            FROM unnest($2::uuid[], $3::uuid[], $4::uuid[], $5::numeric[], $6::numeric[], $7::numeric[], $8::numeric[],
                        $9::text[], $10::text[]) AS u(id, variant_id, return_of, qty, price, cost, discount, unit, code)
            """, connection, tx);
        insert.Parameters.Add(new NpgsqlParameter { Value = saleId });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Id).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.VariantId).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter
        {
            Value = lines.Select(l => l.ReturnOfItemId).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
        });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Qty).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Price).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.CostPrice).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Discount).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Unit).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter
        {
            Value = lines.Select(l => l.ScannedCode).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Повторная отправка: совпало всё, что прислала касса, — тот же чек.</summary>
    private static SaleResult Replay(SaleView existing, NewSale request, DateTimeOffset createdAt)
    {
        var same = existing.Type == request.Type && existing.ShiftId == request.ShiftId && existing.LocalNumber == request.LocalNumber
            && existing.ReturnOfSaleId == request.ReturnOfSaleId && existing.CustomerId == request.CustomerId
            && existing.CreatedAtLocal == createdAt
            && existing.Items.Count == request.Items.Count && existing.Payments.Count == request.Payments.Count;
        if (same)
        {
            var items = existing.Items.ToDictionary(i => i.Id);
            same = request.Items.All(r => items.TryGetValue(r.Id, out var s) && s.Qty == r.Qty && s.ScannedCode == r.ScannedCode
                && (request.Type == SaleTypes.Return
                    ? s.ReturnOfItemId == r.ReturnOfItemId
                    : s.VariantId == r.VariantId && s.Price == r.Price && s.Discount == r.Discount));
            var payments = existing.Payments.ToDictionary(p => p.Id);
            same &= request.Payments.All(r => payments.TryGetValue(r.Id, out var s) && s.Method == r.Method && s.Amount == r.Amount);
        }

        return same
            ? new SaleResult(SaleStatus.AlreadyDone, Sale: existing)
            : new SaleResult(SaleStatus.Conflict, "Чек с таким id уже проведён, и он другой. Проведённый чек не меняется: ошибку исправляет возврат.");
    }

    private static async Task<SaleView?> ReadSaleAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid saleId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, tx);
        batch.BatchCommands.Add(Batch(SaleSql, saleId));
        batch.BatchCommands.Add(Batch(SaleItemsSql, saleId));
        batch.BatchCommands.Add(Batch(PaymentsSql, saleId));
        await using var reader = await batch.ExecuteReaderAsync(ct);
        return await ReadSaleDocumentAsync(reader, ct);
    }

    private static async Task<SaleView?> ReadSaleDocumentAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var id = reader.GetGuid(0);
        var type = reader.GetString(1);
        var store = reader.GetGuid(2);
        var shift = reader.GetGuid(3);
        var device = reader.GetGuid(4);
        var cashier = reader.GetGuid(5);
        var number = reader.GetInt32(6);
        Guid? returnOf = reader.IsDBNull(7) ? null : reader.GetGuid(7);
        Guid? customer = reader.IsDBNull(8) ? null : reader.GetGuid(8);
        var subtotal = reader.GetDecimal(9);
        var discountTotal = reader.GetDecimal(10);
        var total = reader.GetDecimal(11);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(12);

        await reader.NextResultAsync(ct);
        var items = new List<SaleItemView>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new SaleItemView(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8)));
        }

        await reader.NextResultAsync(ct);
        var payments = new List<PaymentView>();
        while (await reader.ReadAsync(ct))
        {
            payments.Add(new PaymentView(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2)));
        }

        return new SaleView(id, type, store, shift, device, cashier, number, returnOf, customer, subtotal, discountTotal, total,
            createdAt, items, payments);
    }

    private static async Task<ShiftView?> ReadShiftAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid shiftId, CancellationToken ct,
        bool forUpdate = false, bool forShare = false)
    {
        var sql = ShiftSql + (forUpdate ? " FOR UPDATE" : forShare ? " FOR SHARE" : "");
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = shiftId });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadShift(reader) : null;
    }

    private static ShiftView ReadShift(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetDecimal(8), reader.IsDBNull(9) ? null : reader.GetDecimal(9));

    /// <summary>Postgres хранит время с точностью до микросекунды, .NET — до 100 нс.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new DateTimeOffset(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg is DBNull
                ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }
                : new NpgsqlParameter { Value = arg });
        }

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlBatchCommand Batch(string sql, Guid id) =>
        new(sql) { Parameters = { new NpgsqlParameter { Value = id } } };
}
