-- =====================================================================
-- Данные для замера отчётов (шаг 7), которых генератор не создаёт:
-- партии со сроком годности, расхождения цен в чеках, поздние возвраты
-- на разбор и смены, изменённые после закрытия. Основной тенант
-- генератора (bench). Повторный запуск ничего не добавляет.
--
--   docker compose exec -T postgres psql -U postgres -d kassir_x3 -X -v ON_ERROR_STOP=1 -f /db/bench/reports_seed.sql
--
-- Только стенд: чеки и смены здесь правятся в обход триггеров
-- (session_replication_role = replica), как в миграциях. В рабочей базе так нельзя.
-- =====================================================================
\set ON_ERROR_STOP on
\set tenant '''c0000000-0000-0000-0000-000000000001'''

BEGIN;

-- 1. Срок годности: каждой 25-й паре — учёт сроков и 2–3 партии прихода в
--    каждом филиале со сроками от −60 до +120 дней от сегодня (около 60 000
--    пар × 3 филиала ≈ 450 000 партий на 1.5 млн пар в базе ×3).
UPDATE product_variants SET tracks_expiry = true
WHERE tenant_id = :tenant AND barcode LIKE '46%' AND NOT tracks_expiry
  AND ('x' || substr(md5(id::text), 1, 8))::bit(32)::int % 25 = 0;

INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason, doc_id, doc_line_id, cost_price, occurred_at, expires_on)
SELECT md5('exp-' || v.id || st.id || n)::uuid, v.tenant_id, st.id, v.id,
       5 + abs(hashtext(v.id::text || n)) % 46, 'purchase',
       md5('expdoc-' || st.id)::uuid, md5('exp-' || v.id || st.id || n)::uuid, v.cost_price,
       now() - interval '200 days',
       current_date - 60 + abs(hashtext(st.id::text || v.id || n)) % 181
FROM product_variants v
JOIN stores st ON st.tenant_id = v.tenant_id
CROSS JOIN generate_series(1, 2 + abs(hashtext(v.id::text)) % 2) n
WHERE v.tenant_id = :tenant AND v.tracks_expiry
ON CONFLICT DO NOTHING;

-- 2–4. Правки чеков и смен — в обход триггеров (только стенд).
SET LOCAL session_replication_role = replica;

-- 2. Расхождение цены: 3 % строк продажи пробиты на 10 сом ниже цены листа.
UPDATE sale_items i SET list_price = i.price + 10, price_mismatch = true
FROM sales s
WHERE s.id = i.sale_id AND s.tenant_id = :tenant AND s.type = 'sale' AND NOT i.price_mismatch
  AND abs(hashtext(i.id::text)) % 100 < 3;

-- 3. Поздние возвраты на разбор: каждый 20-й возврат.
UPDATE sales SET after_close = true, review_reason = 'Возврат больше проданного (стенд отчётов).'
WHERE tenant_id = :tenant AND type = 'return' AND review_reason IS NULL
  AND abs(hashtext(id::text)) % 20 = 0;

-- 4. Смены этих возвратов — «изменены после закрытия».
UPDATE cash_shifts c SET changed_after_close = true
WHERE c.tenant_id = :tenant AND NOT c.changed_after_close
  AND EXISTS (SELECT 1 FROM sales s WHERE s.shift_id = c.id AND s.after_close);

COMMIT;

ANALYZE stock_movements;
ANALYZE sale_items;
ANALYZE sales;
ANALYZE cash_shifts;

SELECT (SELECT count(*) FROM stock_movements WHERE tenant_id = :tenant AND expires_on IS NOT NULL) AS партий_со_сроком,
       (SELECT count(*) FROM sale_items WHERE tenant_id = :tenant AND price_mismatch) AS строк_с_расхождением,
       (SELECT count(*) FROM sales WHERE tenant_id = :tenant AND review_reason IS NOT NULL) AS возвратов_на_разбор,
       (SELECT count(*) FROM cash_shifts WHERE tenant_id = :tenant AND changed_after_close) AS смен_изменено;
