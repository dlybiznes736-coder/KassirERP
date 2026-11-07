-- =====================================================================
-- Миграция 0012: поздние документы (партия C, решение по партии B).
--
-- Поздний документ — чек или возврат, пришедший синхронизацией, когда его
-- смена уже закрыта (например, возврат ждал свой чек с другой кассы).
--   * Принимается в свою закрытую смену (sales.after_close = true).
--   * Ожидаемые наличные смены пересчитываются; посчитанные кассиром
--     (closing_cash) не меняются.
--   * Смена помечается «изменена после закрытия» (changed_after_close),
--     в журнал shift_changes — запись: какой документ, было, стало, кто, когда.
--   * Поздний возврат больше проданного не отклоняется, а принимается с
--     причиной для разбора (sales.review_reason).
-- Пересчёт, пометку и журнал делает база (триггер), приложение не может
-- ни записать в журнал, ни поправить закрытую смену напрямую.
--
-- Меняет правило закрытой схемы «закрытая смена неизменна» (shift_guard):
-- разрешено ровно одно изменение — пересчёт ожидаемых наличных этим триггером.
-- =====================================================================
BEGIN;

ALTER TABLE cash_shifts ADD COLUMN changed_after_close boolean NOT NULL DEFAULT false;

ALTER TABLE sales
  ADD COLUMN after_close   boolean NOT NULL DEFAULT false,  -- принят в уже закрытую смену
  ADD COLUMN review_reason text,                            -- почему нужен разбор (NULL — не нужен)
  ADD CONSTRAINT sales_review_only_late_return
      CHECK (review_reason IS NULL OR (after_close AND type = 'return'));

-- Поздние возвраты на разбор — отчёт.
CREATE INDEX ix_sales_review ON sales (tenant_id, store_id, created_at_local) WHERE review_reason IS NOT NULL;

-- Ожидаемые наличные смены: на начало + наличные продаж − наличные возвратов.
CREATE FUNCTION shift_expected_cash(p_shift uuid) RETURNS numeric
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT c.opening_cash + coalesce((
           SELECT sum(CASE WHEN s.type = 'sale' THEN p.amount ELSE -p.amount END)
           FROM sales s JOIN payments p ON p.sale_id = s.id
           WHERE s.shift_id = c.id AND p.method = 'cash'), 0)
  FROM cash_shifts c WHERE c.id = p_shift
$$;
REVOKE ALL ON FUNCTION shift_expected_cash(uuid) FROM PUBLIC;

-- ---------------------------------------------------------------------
-- Журнал изменений смены после закрытия (append-only, пишет только база)
-- ---------------------------------------------------------------------
CREATE TABLE shift_changes (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL REFERENCES tenants(id),
  shift_id         uuid NOT NULL REFERENCES cash_shifts(id),
  sale_id          uuid NOT NULL REFERENCES sales(id),
  sale_type        text NOT NULL,
  cash_delta       numeric(14,2) NOT NULL,     -- наличные документа (+ продажа, − возврат)
  expected_before  numeric(14,2),
  expected_after   numeric(14,2) NOT NULL,
  changed_by       uuid,                       -- current_user_id(): кто прислал документ
  changed_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_shift_changes_shift ON shift_changes (shift_id);
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON shift_changes FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();
ALTER TABLE shift_changes ENABLE ROW LEVEL SECURITY;
ALTER TABLE shift_changes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON shift_changes USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());
GRANT SELECT ON shift_changes TO kassir_app;

-- ---------------------------------------------------------------------
-- Закрытая смена: единственное разрешённое изменение — пересчёт ожидаемых
-- наличных триггером позднего документа (вложенный вызов, значение
-- совпадает с пересчётом по данным, остальные поля не тронуты).
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION trg_shift_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'closed' THEN
    IF TG_OP = 'UPDATE' AND pg_trigger_depth() > 1
       AND NEW.changed_after_close
       AND NEW.expected_cash IS NOT DISTINCT FROM shift_expected_cash(NEW.id)
       AND (to_jsonb(NEW) - 'expected_cash' - 'changed_after_close')
           = (to_jsonb(OLD) - 'expected_cash' - 'changed_after_close') THEN
      RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Закрытую смену менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN NEW;   -- как в 0007: для DELETE это NULL — удаление открытой смены тихо не выполняется
END $$;

-- Чек — в смену своей кассы и филиала; в закрытую — только как поздний
-- документ (after_close = true), в открытую — только как обычный.
CREATE OR REPLACE FUNCTION trg_sale_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE shift_status text;
BEGIN
  SELECT c.status INTO shift_status FROM cash_shifts c
   WHERE c.id = NEW.shift_id AND c.tenant_id = NEW.tenant_id
     AND c.store_id = NEW.store_id AND c.device_id = NEW.device_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Смена чека — не смена этой кассы' USING ERRCODE = 'foreign_key_violation';
  END IF;
  IF NEW.after_close IS DISTINCT FROM (shift_status = 'closed') THEN
    RAISE EXCEPTION 'Чек в закрытую смену — только как поздний документ' USING ERRCODE = 'check_violation';
  END IF;
  IF NEW.type = 'return' AND NOT EXISTS (
       SELECT 1 FROM sales s WHERE s.id = NEW.return_of_sale_id AND s.tenant_id = NEW.tenant_id AND s.type = 'sale') THEN
    RAISE EXCEPTION 'Возврат — только на чек-продажу своей фирмы' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;

-- Строки чека → движения склада + проверки строк (как в 0007), кроме одного:
-- поздний возврат с причиной разбора может быть больше проданного.
CREATE OR REPLACE FUNCTION trg_sale_item_movement() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE n bigint;
BEGIN
  IF EXISTS (SELECT 1 FROM new_items i
             LEFT JOIN product_variants v ON v.id = i.variant_id AND v.tenant_id = i.tenant_id
             WHERE v.id IS NULL) THEN
    RAISE EXCEPTION 'Строка чека ссылается на пару чужой фирмы' USING ERRCODE = 'foreign_key_violation';
  END IF;

  IF EXISTS (SELECT 1 FROM new_items i JOIN sales s ON s.id = i.sale_id
             WHERE (s.type = 'sale' AND i.return_of_item_id IS NOT NULL)
                OR (s.type = 'return' AND NOT EXISTS (
                      SELECT 1 FROM sale_items o
                      WHERE o.id = i.return_of_item_id AND o.sale_id = s.return_of_sale_id AND o.variant_id = i.variant_id))) THEN
    RAISE EXCEPTION 'Строка возврата — не из исходного чека' USING ERRCODE = 'check_violation';
  END IF;

  IF EXISTS (SELECT 1 FROM (SELECT DISTINCT i.return_of_item_id AS id
                            FROM new_items i JOIN sales s ON s.id = i.sale_id
                            WHERE i.return_of_item_id IS NOT NULL
                              AND NOT (s.after_close AND s.review_reason IS NOT NULL)) r
             JOIN sale_items o ON o.id = r.id
             WHERE (SELECT sum(x.qty) FROM sale_items x WHERE x.return_of_item_id = r.id) > o.qty) THEN
    RAISE EXCEPTION 'Возврат больше проданного' USING ERRCODE = 'check_violation';
  END IF;

  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), i.tenant_id, s.store_id, i.variant_id,
         CASE WHEN s.type = 'return' THEN i.qty ELSE -i.qty END,
         s.type, i.sale_id, i.id, i.cost_price, s.created_at_local, s.cashier_id
  FROM new_items i
  JOIN sales s ON s.id = i.sale_id AND s.tenant_id = i.tenant_id;

  GET DIAGNOSTICS n = ROW_COUNT;
  IF n <> (SELECT count(*) FROM new_items) THEN
    RAISE EXCEPTION 'sale_items.tenant_id не совпадает с чеком' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NULL;
END $$;

-- Поздний документ: строки вставляются последними, оплаты уже есть —
-- пересчитать ожидаемые наличные смены, пометить её, записать в журнал.
CREATE FUNCTION trg_late_document() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  r record;
  before numeric;
  after numeric;
BEGIN
  FOR r IN SELECT DISTINCT s.id, s.tenant_id, s.shift_id, s.type
           FROM new_items i JOIN sales s ON s.id = i.sale_id
           WHERE s.after_close
           ORDER BY s.shift_id, s.id
  LOOP
    SELECT c.expected_cash INTO before FROM cash_shifts c WHERE c.id = r.shift_id;
    after := shift_expected_cash(r.shift_id);
    UPDATE cash_shifts SET expected_cash = after, changed_after_close = true WHERE id = r.shift_id;
    INSERT INTO shift_changes (id, tenant_id, shift_id, sale_id, sale_type, cash_delta, expected_before, expected_after, changed_by)
    SELECT uuid_v7(), r.tenant_id, r.shift_id, r.id, r.type,
           coalesce(sum(CASE WHEN r.type = 'sale' THEN p.amount ELSE -p.amount END), 0), before, after, current_user_id()
    FROM payments p WHERE p.sale_id = r.id AND p.method = 'cash';
  END LOOP;
  RETURN NULL;
END $$;
CREATE TRIGGER late_document AFTER INSERT ON sale_items
  REFERENCING NEW TABLE AS new_items FOR EACH STATEMENT EXECUTE FUNCTION trg_late_document();

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 12;
UPDATE tenants SET schema_version = 12 WHERE schema_version < 12;

COMMIT;
