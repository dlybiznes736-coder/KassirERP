-- Отчёт владельца: выручка, себестоимость, прибыль по дням за год,
-- все филиалы — самый тяжёлый запрос из bench.sql (№ 9). Крутится в цикле.
BEGIN;
SELECT set_config('app.tenant_id', ':tenant', true);
SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       sum(CASE WHEN s.type = 'sale' THEN i.total ELSE -i.total END)                           AS revenue,
       sum(CASE WHEN s.type = 'sale' THEN i.qty * i.cost_price ELSE -i.qty * i.cost_price END) AS cost,
       sum(CASE WHEN s.type = 'sale' THEN 1 ELSE -1 END * (i.total - i.qty * i.cost_price))    AS profit
FROM sales s
JOIN sale_items i ON i.sale_id = s.id
WHERE s.created_at_local >= '2025-11-01 00:00:00+00' AND s.created_at_local < '2026-11-01 00:00:00+00'
GROUP BY 1 ORDER BY 1;
COMMIT;
