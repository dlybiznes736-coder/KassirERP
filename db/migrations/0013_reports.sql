-- =====================================================================
-- Миграция 0013: индексы отчётов (шаг 7). Только индексы.
--
-- ix_stock_negative — отрицательные остатки филиала. Без него отчёт читает
--   все строки остатка филиала (1 млн на базе ×3, 0.55 с); с ним — только
--   строки в минусе. Цена: qty попадает в индексированные колонки, и
--   обновление остатка перестаёт быть HOT (лишняя запись в stock_pkey на
--   каждое обновление). Замер чеков до и после — в отчёте партии C.
-- ix_sale_items_mismatch — строки с расхождением цены: маленький частичный
--   индекс, sale_items только вставляются.
-- ix_shifts_store_opened — смены филиала за период.
-- ix_movements_batches — партии со сроком филиала в порядке (пара, срок) с
--   количеством: отчёт «что истекает» читает только индекс, без сортировки
--   (190 → 140 мс на базе ×3). Частичный: только приходы со сроком.
-- =====================================================================
BEGIN;

CREATE INDEX ix_stock_negative ON stock (tenant_id, store_id, variant_id) WHERE qty < 0;
CREATE INDEX ix_sale_items_mismatch ON sale_items (sale_id) WHERE price_mismatch;
CREATE INDEX ix_shifts_store_opened ON cash_shifts (tenant_id, store_id, opened_at);
CREATE INDEX ix_movements_batches ON stock_movements (tenant_id, store_id, variant_id, expires_on) INCLUDE (qty)
  WHERE expires_on IS NOT NULL AND qty > 0;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 13;
UPDATE tenants SET schema_version = 13 WHERE schema_version < 13;

COMMIT;
