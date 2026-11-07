-- =====================================================================
-- Миграция 0008: прайс-листы и расхождение цены в чеке (партия A).
--
-- 1. price_lists, price_list_items (лист, пара, цена), stores.price_list_id.
--    Цена для филиала = цена его листа, если она есть, иначе базовая
--    (product_variants.sale_price). У филиала без листа — базовая.
-- 2. price_changes — журнал: кто, когда, было, стало. Пишет только база
--    (триггеры уровня оператора: загрузка тысяч цен — одна вставка в
--    журнал). У приложения на журнал — только чтение. Кто изменил —
--    current_user_id() (app.user_id ставит TenantDb из проверенного JWT,
--    там же, где app.tenant_id). NULL — изменение не из API (генератор, SQL).
-- 3. sale_items.list_price и price_mismatch: цена листа на момент приёма
--    чека и пометка, если касса пробила другую. Чек при этом принимается.
-- Только добавление: таблицы, колонка stores.price_list_id, колонки sale_items.
-- =====================================================================
BEGIN;

CREATE FUNCTION current_user_id() RETURNS uuid
LANGUAGE sql STABLE AS $$
  SELECT nullif(current_setting('app.user_id', true), '')::uuid
$$;

CREATE TABLE price_lists (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  name        text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  deleted_at  timestamptz
);
CREATE UNIQUE INDEX ux_price_lists_name ON price_lists (tenant_id, lower(name)) WHERE deleted_at IS NULL;
CREATE TRIGGER set_updated_at BEFORE UPDATE ON price_lists FOR EACH ROW EXECUTE FUNCTION trg_set_updated_at();

CREATE TABLE price_list_items (
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  price_list_id  uuid NOT NULL REFERENCES price_lists(id),
  variant_id     uuid NOT NULL REFERENCES product_variants(id),
  sale_price     numeric(14,2) NOT NULL CHECK (sale_price >= 0),
  updated_at     timestamptz NOT NULL DEFAULT now(),   -- для синхронизации касс (шаг 6)
  PRIMARY KEY (price_list_id, variant_id)
);
CREATE TRIGGER set_updated_at BEFORE UPDATE ON price_list_items FOR EACH ROW EXECUTE FUNCTION trg_set_updated_at();

ALTER TABLE stores ADD COLUMN price_list_id uuid REFERENCES price_lists(id);

CREATE TABLE price_changes (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  price_list_id  uuid REFERENCES price_lists(id),      -- NULL — базовая цена пары
  variant_id     uuid NOT NULL REFERENCES product_variants(id),
  old_price      numeric(14,2),                        -- NULL — цены в листе не было
  new_price      numeric(14,2),                        -- NULL — цену убрали из листа
  changed_by     uuid,                                 -- NULL — не из API
  changed_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_price_changes_variant ON price_changes (tenant_id, variant_id, changed_at);
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON price_changes
  FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();

ALTER TABLE sale_items
  ADD COLUMN list_price numeric(14,2),
  ADD COLUMN price_mismatch boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------------
-- Ссылки — только своей фирмы (FK проверяет существование мимо RLS)
-- ---------------------------------------------------------------------
CREATE FUNCTION trg_price_list_items_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM price_lists l WHERE l.id = NEW.price_list_id AND l.tenant_id = NEW.tenant_id)
     OR NOT EXISTS (SELECT 1 FROM product_variants v WHERE v.id = NEW.variant_id AND v.tenant_id = NEW.tenant_id) THEN
    RAISE EXCEPTION 'Цена листа ссылается на чужой лист или пару' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_list_items_guard BEFORE INSERT OR UPDATE ON price_list_items
  FOR EACH ROW EXECUTE FUNCTION trg_price_list_items_guard();

CREATE FUNCTION trg_store_price_list_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.price_list_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM price_lists l WHERE l.id = NEW.price_list_id AND l.tenant_id = NEW.tenant_id AND l.deleted_at IS NULL) THEN
    RAISE EXCEPTION 'Прайс-лист филиала — не своей фирмы' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER store_price_list_guard BEFORE INSERT OR UPDATE OF price_list_id ON stores
  FOR EACH ROW EXECUTE FUNCTION trg_store_price_list_guard();

-- ---------------------------------------------------------------------
-- Журнал цен. Transition tables нельзя на несколько событий в одном
-- триггере, поэтому три триггера на price_list_items и один на пары.
-- ---------------------------------------------------------------------
CREATE FUNCTION trg_price_journal_insert() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO price_changes (id, tenant_id, price_list_id, variant_id, old_price, new_price, changed_by)
  SELECT uuid_v7(), n.tenant_id, n.price_list_id, n.variant_id, NULL, n.sale_price, current_user_id() FROM new_rows n;
  RETURN NULL;
END $$;

CREATE FUNCTION trg_price_journal_update() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO price_changes (id, tenant_id, price_list_id, variant_id, old_price, new_price, changed_by)
  SELECT uuid_v7(), n.tenant_id, n.price_list_id, n.variant_id, o.sale_price, n.sale_price, current_user_id()
  FROM new_rows n JOIN old_rows o ON o.price_list_id = n.price_list_id AND o.variant_id = n.variant_id
  WHERE o.sale_price IS DISTINCT FROM n.sale_price;
  RETURN NULL;
END $$;

CREATE FUNCTION trg_price_journal_delete() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO price_changes (id, tenant_id, price_list_id, variant_id, old_price, new_price, changed_by)
  SELECT uuid_v7(), o.tenant_id, o.price_list_id, o.variant_id, o.sale_price, NULL, current_user_id() FROM old_rows o;
  RETURN NULL;
END $$;

CREATE TRIGGER price_journal_insert AFTER INSERT ON price_list_items
  REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION trg_price_journal_insert();
CREATE TRIGGER price_journal_update AFTER UPDATE ON price_list_items
  REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION trg_price_journal_update();
CREATE TRIGGER price_journal_delete AFTER DELETE ON price_list_items
  REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION trg_price_journal_delete();

-- Базовая цена пары (price_list_id = NULL в журнале).
CREATE FUNCTION trg_base_price_journal() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO price_changes (id, tenant_id, price_list_id, variant_id, old_price, new_price, changed_by)
  SELECT uuid_v7(), n.tenant_id, NULL, n.id, o.sale_price, n.sale_price, current_user_id()
  FROM new_rows n JOIN old_rows o ON o.id = n.id
  WHERE o.sale_price IS DISTINCT FROM n.sale_price;
  RETURN NULL;
END $$;
CREATE TRIGGER base_price_journal AFTER UPDATE ON product_variants
  REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION trg_base_price_journal();

-- ---------------------------------------------------------------------
-- RLS и права
-- ---------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['price_lists', 'price_list_items', 'price_changes'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id())', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON price_lists TO kassir_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON price_list_items TO kassir_app;
GRANT SELECT ON price_changes TO kassir_app;           -- журнал пишет только база

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 8;
UPDATE tenants SET schema_version = 8 WHERE schema_version < 8;

COMMIT;
