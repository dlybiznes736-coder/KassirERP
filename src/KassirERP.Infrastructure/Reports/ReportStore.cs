using KassirERP.Application.Reports;
using KassirERP.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace KassirERP.Infrastructure.Reports;

/// <summary>
/// Отчёты через TenantDb (RLS). Приёмы, найденные замером на базе ×3:
/// * период по индексу (тенант, филиал, время) с запасом ±14 ч, день — по
///   местному времени филиала отдельным условием;
/// * название пары — LATERAL … LIMIT 1 по первичному ключу: без этого
///   планировщик соединял сотни строк с 1.5 млн пар полным чтением;
/// * страница выбирается до подтягивания названий (MATERIALIZED).
/// </summary>
internal sealed class ReportStore(TenantDb db) : IReportStore
{
    private const string NameLateral =
        "CROSS JOIN LATERAL (SELECT p.name, v.barcode, v.unit FROM product_variants v JOIN products p ON p.id = v.product_id WHERE v.id = {0} LIMIT 1) x";

    // Период: $1, $2 — границы по индексу (UTC с запасом), $3, $4 — дни по местному времени.
    private const string PeriodWhere =
        """
            {0}.created_at_local >= $1 AND {0}.created_at_local < $2
        AND ({0}.created_at_local AT TIME ZONE st.timezone)::date BETWEEN $3 AND $4
        """;

    public Task<IReadOnlyList<SalesDayRow>> SalesAsync(ReportPeriod period, CancellationToken ct)
    {
        var sql =
            $"""
            SELECT (s.created_at_local AT TIME ZONE st.timezone)::date AS day, s.store_id,
                   (count(*) FILTER (WHERE s.type = 'sale'))::int, (count(*) FILTER (WHERE s.type = 'return'))::int,
                   coalesce(sum(s.total) FILTER (WHERE s.type = 'sale'), 0), coalesce(sum(s.total) FILTER (WHERE s.type = 'return'), 0),
                   round(coalesce(sum(c.cost) FILTER (WHERE s.type = 'sale'), 0) - coalesce(sum(c.cost) FILTER (WHERE s.type = 'return'), 0), 2)
            FROM sales s
            JOIN stores st ON st.id = s.store_id
            CROSS JOIN LATERAL (SELECT sum(i.cost_price * i.qty) AS cost FROM sale_items i WHERE i.sale_id = s.id) c
            WHERE {Period("s")} {StoreFilter(period, "s")}
            GROUP BY 1, 2
            ORDER BY 1, 2
            """;
        return db.ReadAsync<IReadOnlyList<SalesDayRow>>([PeriodCommand(sql, period)], async reader =>
        {
            var rows = new List<SalesDayRow>();
            while (await reader.ReadAsync(ct))
            {
                var revenue = reader.GetDecimal(4);
                var refunds = reader.GetDecimal(5);
                var cost = reader.GetDecimal(6);
                rows.Add(new SalesDayRow(reader.GetFieldValue<DateOnly>(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3),
                    revenue, refunds, revenue - refunds, cost, revenue - refunds - cost));
            }

            return rows;
        }, ct);
    }

    public Task<ReportPage<StockRow>> StockAsync(Guid storeId, Guid? after, int limit, CancellationToken ct) =>
        db.ReadAsync([Command(
            """
            SELECT s.variant_id, v.product_id, p.name, v.size, v.color, v.barcode, v.unit, s.qty, v.cost_price, v.sale_price
            FROM stock s
            JOIN product_variants v ON v.id = s.variant_id
            JOIN products p ON p.id = v.product_id
            WHERE s.store_id = $1 AND s.variant_id > $2 AND s.qty <> 0
            ORDER BY s.variant_id
            LIMIT $3
            """, storeId, after ?? Guid.Empty, limit + 1)], async reader =>
        {
            var rows = new List<StockRow>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new StockRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                    reader.GetString(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9)));
            }

            return Page(rows, limit, r => r.VariantId.ToString("N"));
        }, ct);

    public Task<ReportPage<ExpiringRow>> ExpiringAsync(
        Guid storeId, int days, bool includeExpired, (DateOnly ExpiresOn, Guid VariantId)? after, int limit, CancellationToken ct)
    {
        // Остаток партии по FIFO по сроку: продаётся сначала то, что истекает раньше,
        // поэтому остаток пары лежит в самых поздних партиях. Для партии:
        // осталось = min(партия, max(остаток пары − партии с более поздним сроком, 0)).
        // Партии раньше нижней границы на это не влияют — их не читаем.
        //
        // Замер на базе ×3 (100 000 будущих партий в филиале): партии — из индекса
        // ix_movements_batches уже в порядке (пара, срок), «более поздние партии» —
        // одна оконная функция без сортировки; остаток пары ищется только для
        // кандидатов по порядку срока, пока не наберётся страница (а не для всех
        // 25 000 партий окна в 30 дней): 140 мс вместо 490.
        var sql =
            $"""
            WITH t AS (SELECT (now() AT TIME ZONE timezone)::date AS today FROM stores WHERE id = $1),
            r AS (
              SELECT variant_id, expires_on, qty,
                     coalesce(sum(qty) OVER (PARTITION BY variant_id ORDER BY expires_on
                                             ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING), 0) AS newer
              FROM (SELECT m.variant_id, m.expires_on, sum(m.qty) AS qty
                    FROM stock_movements m, t
                    WHERE m.store_id = $1 AND m.expires_on IS NOT NULL AND m.qty > 0 AND m.expires_on >= t.today - $3
                    GROUP BY 1, 2) b),
            page AS MATERIALIZED (
              SELECT w.variant_id, w.expires_on, l.left_qty
              FROM (SELECT r.* FROM r, t
                    WHERE r.expires_on <= t.today + $2 AND (r.expires_on, r.variant_id) > ($4, $5)
                    ORDER BY r.expires_on, r.variant_id) w
              CROSS JOIN LATERAL (
                SELECT least(w.qty, greatest(coalesce(
                         (SELECT s.qty FROM stock s WHERE s.store_id = $1 AND s.variant_id = w.variant_id), 0) - w.newer, 0)) AS left_qty) l
              WHERE l.left_qty > 0
              LIMIT $6)
            SELECT pg.variant_id, x.name, x.barcode, x.unit, pg.expires_on, (pg.expires_on - t.today)::int, pg.left_qty
            FROM page pg CROSS JOIN t
            {string.Format(NameLateral, "pg.variant_id")}
            ORDER BY pg.expires_on, pg.variant_id
            """;
        var cursor = after ?? (DateOnly.MinValue, Guid.Empty);
        return db.ReadAsync([Command(sql, storeId, days, includeExpired ? 90 : 0, cursor.ExpiresOn, cursor.VariantId, limit + 1)], async reader =>
        {
            var rows = new List<ExpiringRow>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ExpiringRow(reader.GetGuid(0), reader.GetString(1), Text(reader, 2), reader.GetString(3),
                    reader.GetFieldValue<DateOnly>(4), reader.GetInt32(5), reader.GetDecimal(6)));
            }

            return Page(rows, limit, r => ReportService.Cursor(r.ExpiresOn, r.VariantId));
        }, ct);
    }

    public Task<ReportPage<NegativeStockRow>> NegativeStockAsync(Guid storeId, (decimal Qty, Guid VariantId)? after, int limit, CancellationToken ct)
    {
        // Частичный индекс ix_stock_negative: читаются только строки в минусе.
        var sql =
            $"""
            WITH n AS MATERIALIZED (
              SELECT variant_id, qty FROM stock
              WHERE store_id = $1 AND qty < 0 AND (qty, variant_id) > ($2, $3)
              ORDER BY qty, variant_id
              LIMIT $4)
            SELECT n.variant_id, x.name, x.barcode, x.unit, n.qty
            FROM n {string.Format(NameLateral, "n.variant_id")}
            ORDER BY n.qty, n.variant_id
            """;
        var cursor = after ?? (-1_000_000_000_000_000m, Guid.Empty);
        return db.ReadAsync([Command(sql, storeId, cursor.Qty, cursor.VariantId, limit + 1)], async reader =>
        {
            var rows = new List<NegativeStockRow>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new NegativeStockRow(reader.GetGuid(0), reader.GetString(1), Text(reader, 2), reader.GetString(3), reader.GetDecimal(4)));
            }

            return Page(rows, limit, r => ReportService.Cursor(r.Qty, r.VariantId));
        }, ct);
    }

    public Task<IReadOnlyList<PriceMismatchRow>> PriceMismatchesAsync(ReportPeriod period, int limit, CancellationToken ct)
    {
        var sql =
            $"""
            WITH m AS MATERIALIZED (
              SELECT s.id, s.store_id, s.device_id, s.cashier_id, s.local_number, s.created_at_local,
                     i.id AS item_id, i.variant_id, i.qty, i.price, i.list_price
              FROM sales s
              JOIN stores st ON st.id = s.store_id
              JOIN sale_items i ON i.sale_id = s.id AND i.price_mismatch
              WHERE {Period("s")} {StoreFilter(period, "s")}
              ORDER BY s.created_at_local, s.id, i.id
              LIMIT {limit})
            SELECT m.id, m.store_id, m.device_id, m.cashier_id, m.local_number, m.created_at_local, m.variant_id, x.name,
                   m.qty, m.price, m.list_price
            FROM m {string.Format(NameLateral, "m.variant_id")}
            ORDER BY m.created_at_local, m.id, m.item_id
            """;
        return db.ReadAsync<IReadOnlyList<PriceMismatchRow>>([PeriodCommand(sql, period)], async reader =>
        {
            var rows = new List<PriceMismatchRow>();
            while (await reader.ReadAsync(ct))
            {
                var qty = reader.GetDecimal(8);
                var price = reader.GetDecimal(9);
                var list = reader.GetDecimal(10);
                rows.Add(new PriceMismatchRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetInt32(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(6), reader.GetString(7), qty, price, list,
                    decimal.Round((list - price) * qty, 2, MidpointRounding.AwayFromZero)));
            }

            return rows;
        }, ct);
    }

    public Task<IReadOnlyList<ShiftRow>> ShiftsAsync(ReportPeriod period, bool changedOnly, CancellationToken ct)
    {
        var sql =
            $"""
            SELECT c.id, c.store_id, c.device_id, c.cashier_id, c.status, c.opened_at, c.closed_at,
                   c.opening_cash, c.closing_cash, c.expected_cash, c.changed_after_close,
                   (SELECT count(*) FROM shift_changes x WHERE x.shift_id = c.id)::int
            FROM cash_shifts c
            JOIN stores st ON st.id = c.store_id
            WHERE c.opened_at >= $1 AND c.opened_at < $2
              AND (c.opened_at AT TIME ZONE st.timezone)::date BETWEEN $3 AND $4
              {StoreFilter(period, "c")} {(changedOnly ? "AND c.changed_after_close" : "")}
            ORDER BY c.opened_at, c.id
            """;
        return db.ReadAsync<IReadOnlyList<ShiftRow>>([PeriodCommand(sql, period)], async reader =>
        {
            var rows = new List<ShiftRow>();
            while (await reader.ReadAsync(ct))
            {
                decimal? closing = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
                decimal? expected = reader.IsDBNull(9) ? null : reader.GetDecimal(9);
                rows.Add(new ShiftRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetDecimal(7), closing, expected, closing - expected, reader.GetBoolean(10), reader.GetInt32(11)));
            }

            return rows;
        }, ct);
    }

    public Task<IReadOnlyList<ReviewRow>> ReviewAsync(ReportPeriod period, CancellationToken ct)
    {
        var sql =
            $"""
            SELECT s.id, s.store_id, s.shift_id, s.device_id, s.cashier_id, s.local_number, s.created_at_local,
                   s.return_of_sale_id, s.total, s.review_reason
            FROM sales s
            JOIN stores st ON st.id = s.store_id
            WHERE s.review_reason IS NOT NULL AND {Period("s")} {StoreFilter(period, "s")}
            ORDER BY s.created_at_local, s.id
            """;
        return db.ReadAsync<IReadOnlyList<ReviewRow>>([PeriodCommand(sql, period)], async reader =>
        {
            var rows = new List<ReviewRow>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ReviewRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
                    reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetGuid(7), reader.GetDecimal(8), reader.GetString(9)));
            }

            return rows;
        }, ct);
    }

    public Task<bool> StoreExistsAsync(Guid storeId, CancellationToken ct) =>
        db.ReadAsync([Command("SELECT 1 FROM stores WHERE id = $1 AND deleted_at IS NULL", storeId)],
            async reader => await reader.ReadAsync(ct), ct);

    // -----------------------------------------------------------------------

    private static string Period(string alias) => string.Format(PeriodWhere, alias);

    /// <summary>Филиал — отдельным текстом запроса, а не «$5 IS NULL OR …»: иначе общий план не берёт индекс по филиалу.</summary>
    private static string StoreFilter(ReportPeriod period, string alias) =>
        period.StoreId is { } id ? $"AND {alias}.store_id = '{id:D}'" : "";

    /// <summary>
    /// Границы периода: дни from..to по местному времени любого часового пояса
    /// (от −12 до +14) лежат внутри [from 00:00 UTC − 14 ч, to+1 00:00 UTC + 14 ч).
    /// </summary>
    private static NpgsqlBatchCommand PeriodCommand(string sql, ReportPeriod period)
    {
        var lower = new DateTimeOffset(period.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14);
        var upper = new DateTimeOffset(period.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(14);
        return Command(sql, lower, upper, period.From, period.To);
    }

    private static NpgsqlBatchCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg is DateOnly date
                ? new NpgsqlParameter { Value = date, NpgsqlDbType = NpgsqlDbType.Date }
                : new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static ReportPage<T> Page<T>(List<T> rows, int limit, Func<T, string> cursor)
    {
        if (rows.Count <= limit)
        {
            return new ReportPage<T>(rows, null);
        }

        rows.RemoveAt(rows.Count - 1);
        return new ReportPage<T>(rows, cursor(rows[^1]));
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
