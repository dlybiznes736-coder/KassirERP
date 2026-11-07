-- =====================================================================
-- Миграция 0006: приём товара со сроком годности (шаг 4, DESIGN_GROCERY.md п. 2).
--
-- Минимум: срок хранится у строки накладной и копируется в движение при
-- проведении. Партии при продаже не ведутся; «что скоро истечёт» —
-- оценка FIFO по приходам (отчёт — позже). Только добавление колонок:
-- журнал движений не переписывается (колонка без значения по умолчанию).
-- =====================================================================
BEGIN;

ALTER TABLE product_variants ADD COLUMN tracks_expiry boolean NOT NULL DEFAULT false;
ALTER TABLE purchase_items   ADD COLUMN expires_on date;
ALTER TABLE stock_movements  ADD COLUMN expires_on date;

-- «Истекает в ближайшие N дней» по филиалу: только приходы со сроком.
CREATE INDEX ix_movements_expiry ON stock_movements (tenant_id, store_id, expires_on)
  WHERE expires_on IS NOT NULL AND qty > 0;

-- Проведение накладной копирует срок в движение.
CREATE OR REPLACE FUNCTION trg_purchase_accept() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by, expires_on)
  SELECT uuid_v7(), pi.tenant_id, n.store_id, pi.variant_id, pi.qty, 'purchase',
         n.id, pi.id, pi.cost_price, n.accepted_at, n.accepted_by, pi.expires_on
  FROM new_purchases n
  JOIN old_purchases o ON o.id = n.id
  JOIN purchase_items pi ON pi.purchase_id = n.id
  WHERE o.status = 'draft' AND n.status = 'accepted';
  RETURN NULL;
END $$;

-- Строки принятой накладной не меняются (как было) + строка ссылается только
-- на пару и накладную своей фирмы. FK проверяет существование мимо RLS и
-- пропустил бы пару чужой фирмы; API это тоже проверяет, база — страховка.
CREATE OR REPLACE FUNCTION trg_purchase_items_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE p purchases;
BEGIN
  SELECT * INTO p FROM purchases
   WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.purchase_id ELSE NEW.purchase_id END;
  IF p.status = 'accepted' THEN
    RAISE EXCEPTION 'Строки принятой накладной менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  IF TG_OP = 'DELETE' THEN
    RETURN OLD;
  END IF;
  IF p.tenant_id IS DISTINCT FROM NEW.tenant_id
     OR NOT EXISTS (SELECT 1 FROM product_variants v WHERE v.id = NEW.variant_id AND v.tenant_id = NEW.tenant_id) THEN
    RAISE EXCEPTION 'Строка накладной ссылается на чужую накладную или пару' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 6;
UPDATE tenants SET schema_version = 6 WHERE schema_version < 6;

COMMIT;
