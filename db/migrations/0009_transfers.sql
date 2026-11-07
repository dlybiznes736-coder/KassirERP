-- =====================================================================
-- Миграция 0009: перемещение между филиалами (партия A).
--
-- Документ: draft → in_transit (отправлен) → received (принят).
-- Отправка: движение transfer_out −qty_sent в филиале-отправителе.
-- Приём:    движение transfer_in  +qty_received в филиале-получателе.
-- Принято меньше, чем отправлено, — расхождение остаётся в строке
-- (qty_sent и qty_received) и в transfers.has_discrepancy. Остатки обоих
-- филиалов отражают физическую реальность: недостача не появляется ни
-- в одном из них, её разбирают по документу.
-- Движения пишет база (триггер уровня оператора), один раз на строку
-- (ux_movements_doc_line: reason + doc_line_id) — повтор не дублирует.
-- Причины transfer_out / transfer_in заложены в схеме v1.
-- Только добавление: две новые таблицы.
-- =====================================================================
BEGIN;

CREATE TABLE transfers (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL REFERENCES tenants(id),
  from_store_id    uuid NOT NULL REFERENCES stores(id),
  to_store_id      uuid NOT NULL REFERENCES stores(id),
  number           text,
  status           text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'in_transit', 'received')),
  has_discrepancy  boolean NOT NULL DEFAULT false,
  created_by       uuid REFERENCES users(id),
  sent_by          uuid REFERENCES users(id),
  sent_at          timestamptz,
  received_by      uuid REFERENCES users(id),
  received_at      timestamptz,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  CHECK (from_store_id <> to_store_id),
  CHECK ((status = 'draft') = (sent_at IS NULL)),
  CHECK ((status = 'received') = (received_at IS NOT NULL))
);
CREATE INDEX ix_transfers_in_transit ON transfers (tenant_id, to_store_id) WHERE status = 'in_transit';
CREATE TRIGGER set_updated_at BEFORE UPDATE ON transfers FOR EACH ROW EXECUTE FUNCTION trg_set_updated_at();

CREATE TABLE transfer_items (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  transfer_id   uuid NOT NULL REFERENCES transfers(id),
  variant_id    uuid NOT NULL REFERENCES product_variants(id),
  qty_sent      numeric(14,3) NOT NULL CHECK (qty_sent > 0),
  qty_received  numeric(14,3) CHECK (qty_received >= 0 AND qty_received <= qty_sent),
  cost_price    numeric(14,2) NOT NULL CHECK (cost_price >= 0)   -- себестоимость на момент отправки
);
CREATE INDEX ix_transfer_items_transfer ON transfer_items (transfer_id);

-- ---------------------------------------------------------------------
-- Правила документа
-- ---------------------------------------------------------------------

-- Статус: только draft → in_transit → received; принятый не меняется.
CREATE FUNCTION trg_transfer_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    IF OLD.status <> 'draft' THEN
      RAISE EXCEPTION 'Отправленное перемещение удалить нельзя' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN OLD;
  END IF;

  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'draft' THEN
      RAISE EXCEPTION 'Перемещение создаётся черновиком' USING ERRCODE = 'restrict_violation';
    END IF;
  ELSIF OLD.status = 'received' THEN
    RAISE EXCEPTION 'Принятое перемещение менять нельзя' USING ERRCODE = 'restrict_violation';
  ELSIF NOT (OLD.status = NEW.status
             OR (OLD.status = 'draft' AND NEW.status = 'in_transit')
             OR (OLD.status = 'in_transit' AND NEW.status = 'received')) THEN
    RAISE EXCEPTION 'Перемещение: только черновик → в пути → принято' USING ERRCODE = 'restrict_violation';
  ELSIF OLD.status <> 'draft' AND (NEW.from_store_id, NEW.to_store_id) IS DISTINCT FROM (OLD.from_store_id, OLD.to_store_id) THEN
    RAISE EXCEPTION 'Филиалы отправленного перемещения менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;

  IF NOT EXISTS (SELECT 1 FROM stores s WHERE s.id = NEW.from_store_id AND s.tenant_id = NEW.tenant_id)
     OR NOT EXISTS (SELECT 1 FROM stores s WHERE s.id = NEW.to_store_id AND s.tenant_id = NEW.tenant_id) THEN
    RAISE EXCEPTION 'Перемещение — только между филиалами своей фирмы' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER transfer_guard BEFORE INSERT OR UPDATE OR DELETE ON transfers
  FOR EACH ROW EXECUTE FUNCTION trg_transfer_guard();

-- Строки: в черновике — что угодно; в пути — только один раз проставить
-- qty_received; после приёма — ничего. Пара — своей фирмы.
CREATE FUNCTION trg_transfer_items_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE t transfers;
BEGIN
  SELECT * INTO t FROM transfers
   WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.transfer_id ELSE NEW.transfer_id END;

  IF t.status = 'in_transit' AND TG_OP = 'UPDATE'
     AND OLD.qty_received IS NULL AND NEW.qty_received IS NOT NULL
     AND (NEW.id, NEW.transfer_id, NEW.variant_id, NEW.qty_sent, NEW.cost_price)
         = (OLD.id, OLD.transfer_id, OLD.variant_id, OLD.qty_sent, OLD.cost_price) THEN
    RETURN NEW;   -- приём: проставляем принятое количество
  END IF;

  IF t.status <> 'draft' THEN
    RAISE EXCEPTION 'Строки отправленного перемещения менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  IF TG_OP = 'DELETE' THEN
    RETURN OLD;
  END IF;
  IF t.tenant_id IS DISTINCT FROM NEW.tenant_id
     OR NOT EXISTS (SELECT 1 FROM product_variants v WHERE v.id = NEW.variant_id AND v.tenant_id = NEW.tenant_id) THEN
    RAISE EXCEPTION 'Строка перемещения ссылается на чужой документ или пару' USING ERRCODE = 'foreign_key_violation';
  END IF;
  IF NEW.qty_received IS NOT NULL THEN
    RAISE EXCEPTION 'Принятое количество проставляется при приёме' USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER transfer_items_guard BEFORE INSERT OR UPDATE OR DELETE ON transfer_items
  FOR EACH ROW EXECUTE FUNCTION trg_transfer_items_guard();

-- Движения склада при отправке и приёме. Все документы одного UPDATE —
-- один INSERT движений → одно обновление stock на пару (как у прихода).
CREATE FUNCTION trg_transfer_movements() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  -- Отправка: −qty_sent в филиале-отправителе.
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), i.tenant_id, n.from_store_id, i.variant_id, -i.qty_sent, 'transfer_out',
         n.id, i.id, i.cost_price, n.sent_at, n.sent_by
  FROM new_transfers n
  JOIN old_transfers o ON o.id = n.id
  JOIN transfer_items i ON i.transfer_id = n.id
  WHERE o.status = 'draft' AND n.status = 'in_transit';

  -- Приём: +qty_received в филиале-получателе (ноль — движения нет).
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), i.tenant_id, n.to_store_id, i.variant_id, i.qty_received, 'transfer_in',
         n.id, i.id, i.cost_price, n.received_at, n.received_by
  FROM new_transfers n
  JOIN old_transfers o ON o.id = n.id
  JOIN transfer_items i ON i.transfer_id = n.id
  WHERE o.status = 'in_transit' AND n.status = 'received' AND i.qty_received > 0;

  RETURN NULL;
END $$;
CREATE TRIGGER transfer_movements AFTER UPDATE ON transfers
  REFERENCING OLD TABLE AS old_transfers NEW TABLE AS new_transfers
  FOR EACH STATEMENT EXECUTE FUNCTION trg_transfer_movements();

-- ---------------------------------------------------------------------
-- RLS и права
-- ---------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['transfers', 'transfer_items'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id())', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON transfers, transfer_items TO kassir_app;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 9;
UPDATE tenants SET schema_version = 9 WHERE schema_version < 9;

COMMIT;
