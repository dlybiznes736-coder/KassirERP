-- =====================================================================
-- Миграция 0010: списание и инвентаризация (партия B).
--
-- Списание — документ с причиной (spoilage — порча, expired — просрочка,
-- defect — брак), проводится сразу при создании: движение writeoff −qty.
--
-- Инвентаризация: открыть → подсчитать → провести. Для каждой пары при
-- подсчёте запоминаются посчитанное количество И учётный остаток в этот
-- момент (book_qty). Проведение пишет движение inventory на разницу
-- counted − book. Продажа во время инвентаризации не теряется: если она
-- была до подсчёта пары — она уже в book_qty и в физическом остатке; если
-- после — уменьшает остаток обычным движением, а разница её не трогает.
-- (Подход «остаток := посчитанное по снимку на начало» терял бы такие продажи.)
-- Остаток не перезаписывается: только движения, журнал по-прежнему append-only.
-- Причины writeoff / inventory заложены в схеме v1. Только новые таблицы.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- Списание
-- ---------------------------------------------------------------------
CREATE TABLE writeoffs (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  store_id    uuid NOT NULL REFERENCES stores(id),
  reason      text NOT NULL CHECK (reason IN ('spoilage', 'expired', 'defect')),
  note        text,
  created_by  uuid REFERENCES users(id),
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE writeoff_items (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  writeoff_id  uuid NOT NULL REFERENCES writeoffs(id),
  variant_id   uuid NOT NULL REFERENCES product_variants(id),
  qty          numeric(14,3) NOT NULL CHECK (qty > 0),
  cost_price   numeric(14,2) NOT NULL CHECK (cost_price >= 0)
);
CREATE INDEX ix_writeoff_items_writeoff ON writeoff_items (writeoff_id);

-- Проведённое списание не меняется (как чек).
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON writeoffs FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON writeoff_items FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();

-- Строки → движения writeoff, одним оператором (одно обновление stock на пару).
CREATE FUNCTION trg_writeoff_movements() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM new_items i
             LEFT JOIN writeoffs w ON w.id = i.writeoff_id AND w.tenant_id = i.tenant_id
             LEFT JOIN product_variants v ON v.id = i.variant_id AND v.tenant_id = i.tenant_id
             WHERE w.id IS NULL OR v.id IS NULL) THEN
    RAISE EXCEPTION 'Строка списания ссылается на чужой документ или пару' USING ERRCODE = 'foreign_key_violation';
  END IF;

  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), i.tenant_id, w.store_id, i.variant_id, -i.qty, 'writeoff',
         w.id, i.id, i.cost_price, w.created_at, w.created_by
  FROM new_items i JOIN writeoffs w ON w.id = i.writeoff_id;
  RETURN NULL;
END $$;
CREATE TRIGGER writeoff_movements AFTER INSERT ON writeoff_items
  REFERENCING NEW TABLE AS new_items FOR EACH STATEMENT EXECUTE FUNCTION trg_writeoff_movements();

-- ---------------------------------------------------------------------
-- Инвентаризация
-- ---------------------------------------------------------------------
CREATE TABLE inventories (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  store_id    uuid NOT NULL REFERENCES stores(id),
  number      text,
  status      text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'posted')),
  opened_by   uuid REFERENCES users(id),
  opened_at   timestamptz NOT NULL DEFAULT now(),
  posted_by   uuid REFERENCES users(id),
  posted_at   timestamptz,
  CHECK ((status = 'posted') = (posted_at IS NOT NULL))
);
-- Одна открытая инвентаризация на филиал: пересчёты не пересекаются.
CREATE UNIQUE INDEX ux_inventories_open_per_store ON inventories (tenant_id, store_id) WHERE status = 'open';

CREATE TABLE inventory_items (
  id            uuid PRIMARY KEY,                       -- строка движения (doc_line_id)
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  inventory_id  uuid NOT NULL REFERENCES inventories(id),
  variant_id    uuid NOT NULL REFERENCES product_variants(id),
  counted_qty   numeric(14,3) NOT NULL CHECK (counted_qty >= 0),
  book_qty      numeric(14,3) NOT NULL,                 -- учётный остаток в момент подсчёта
  cost_price    numeric(14,2) NOT NULL CHECK (cost_price >= 0),
  counted_by    uuid REFERENCES users(id),
  counted_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (inventory_id, variant_id)                     -- пересчёт пары перезаписывает строку
);

-- Проведённую не меняем; строки — только пока открыта; пара — своей фирмы.
CREATE FUNCTION trg_inventory_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' OR OLD.status = 'posted' THEN
    RAISE EXCEPTION 'Инвентаризацию нельзя удалить, проведённую — изменить' USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER inventory_guard BEFORE UPDATE OR DELETE ON inventories FOR EACH ROW EXECUTE FUNCTION trg_inventory_guard();

CREATE FUNCTION trg_inventory_items_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE inv inventories;
BEGIN
  SELECT * INTO inv FROM inventories
   WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.inventory_id ELSE NEW.inventory_id END;
  IF inv.status <> 'open' THEN
    RAISE EXCEPTION 'Строки проведённой инвентаризации менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  IF TG_OP = 'DELETE' THEN
    RETURN OLD;
  END IF;
  IF inv.tenant_id IS DISTINCT FROM NEW.tenant_id
     OR NOT EXISTS (SELECT 1 FROM product_variants v WHERE v.id = NEW.variant_id AND v.tenant_id = NEW.tenant_id) THEN
    RAISE EXCEPTION 'Строка инвентаризации ссылается на чужой документ или пару' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER inventory_items_guard BEFORE INSERT OR UPDATE OR DELETE ON inventory_items
  FOR EACH ROW EXECUTE FUNCTION trg_inventory_items_guard();

-- Проведение: движение inventory на разницу counted − book (нулевая — без движения).
CREATE FUNCTION trg_inventory_post() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), i.tenant_id, n.store_id, i.variant_id, i.counted_qty - i.book_qty, 'inventory',
         n.id, i.id, i.cost_price, n.posted_at, n.posted_by
  FROM new_inv n
  JOIN old_inv o ON o.id = n.id
  JOIN inventory_items i ON i.inventory_id = n.id
  WHERE o.status = 'open' AND n.status = 'posted' AND i.counted_qty <> i.book_qty;
  RETURN NULL;
END $$;
CREATE TRIGGER inventory_post AFTER UPDATE ON inventories
  REFERENCING OLD TABLE AS old_inv NEW TABLE AS new_inv FOR EACH STATEMENT EXECUTE FUNCTION trg_inventory_post();

-- ---------------------------------------------------------------------
-- RLS и права
-- ---------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['writeoffs', 'writeoff_items', 'inventories', 'inventory_items'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id())', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON writeoffs, writeoff_items TO kassir_app;
GRANT SELECT, INSERT, UPDATE ON inventories, inventory_items TO kassir_app;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 10;
UPDATE tenants SET schema_version = 10 WHERE schema_version < 10;

COMMIT;
