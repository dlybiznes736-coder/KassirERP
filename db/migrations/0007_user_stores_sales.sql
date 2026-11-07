-- =====================================================================
-- Миграция 0007: доступ к филиалам — явный список; защита продаж (шаг 5).
--
-- 1. user_stores — филиалы, в которых сотрудник может работать за кассой.
--    Вход по PIN: филиал кассы должен быть в списке; пустой список — вход
--    запрещён (раньше users.store_id = NULL значило «все филиалы»).
--    Заполняется из users.store_id. users.store_id остаётся филиалом
--    по умолчанию для входа в веб по паролю.
-- 2. Продажи. Проведённый чек не меняется: запрет UPDATE на sales (DELETE
--    был запрещён и раньше; строки и оплаты — с версии 1). Чек — только в
--    смену своей кассы и своего филиала. Возврат — только на чек-продажу
--    своей фирмы, строки возврата — из исходного чека, вернуть можно не
--    больше проданного. Пары в строках — только своей фирмы. Закрытая
--    смена не меняется.
-- Проверки в базе — страховка: API проверяет то же самое раньше и
-- отвечает понятной ошибкой.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- 1. Доступ к филиалам
-- ---------------------------------------------------------------------
CREATE TABLE user_stores (
  tenant_id  uuid NOT NULL REFERENCES tenants(id),
  user_id    uuid NOT NULL REFERENCES users(id),
  store_id   uuid NOT NULL REFERENCES stores(id),
  PRIMARY KEY (user_id, store_id)
);
ALTER TABLE user_stores ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_stores FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON user_stores
  USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());
GRANT SELECT, INSERT, DELETE ON user_stores TO kassir_app;

-- Сотрудник и филиал — одной фирмы (FK проверяет только существование).
CREATE FUNCTION trg_user_stores_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM users u JOIN stores s ON s.tenant_id = u.tenant_id
                 WHERE u.id = NEW.user_id AND s.id = NEW.store_id AND u.tenant_id = NEW.tenant_id) THEN
    RAISE EXCEPTION 'Сотрудник и филиал — разных фирм' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER user_stores_guard BEFORE INSERT ON user_stores
  FOR EACH ROW EXECUTE FUNCTION trg_user_stores_guard();

INSERT INTO user_stores (tenant_id, user_id, store_id)
SELECT tenant_id, id, store_id FROM users WHERE store_id IS NOT NULL
ON CONFLICT DO NOTHING;

-- Вход по PIN: есть ли филиал кассы в списке сотрудника (до токена — мимо RLS).
CREATE FUNCTION auth.user_has_store(p_user uuid, p_store uuid)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT EXISTS (SELECT 1 FROM user_stores WHERE user_id = p_user AND store_id = p_store)
$$;
REVOKE ALL ON FUNCTION auth.user_has_store(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.user_has_store(uuid, uuid) TO kassir_app;

-- ---------------------------------------------------------------------
-- 2. Продажи
-- ---------------------------------------------------------------------

-- Проведённый чек не меняется: UPDATE запрещён, как и DELETE.
-- (Миграции, которым нужно переписать чеки, работают с session_replication_role = replica.)
DROP TRIGGER forbid_change ON sales;
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON sales
  FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();
REVOKE UPDATE ON sales FROM kassir_app;

-- Чек — в смену своей кассы и филиала; возврат — на продажу своей фирмы.
CREATE FUNCTION trg_sale_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM cash_shifts c
                 WHERE c.id = NEW.shift_id AND c.tenant_id = NEW.tenant_id
                   AND c.store_id = NEW.store_id AND c.device_id = NEW.device_id) THEN
    RAISE EXCEPTION 'Смена чека — не смена этой кассы' USING ERRCODE = 'foreign_key_violation';
  END IF;
  IF NEW.type = 'return' AND NOT EXISTS (
       SELECT 1 FROM sales s WHERE s.id = NEW.return_of_sale_id AND s.tenant_id = NEW.tenant_id AND s.type = 'sale') THEN
    RAISE EXCEPTION 'Возврат — только на чек-продажу своей фирмы' USING ERRCODE = 'foreign_key_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sale_guard BEFORE INSERT ON sales
  FOR EACH ROW EXECUTE FUNCTION trg_sale_guard();

-- Закрытая смена не меняется (и не открывается заново).
CREATE FUNCTION trg_shift_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'closed' THEN
    RAISE EXCEPTION 'Закрытую смену менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER shift_guard BEFORE UPDATE OR DELETE ON cash_shifts
  FOR EACH ROW EXECUTE FUNCTION trg_shift_guard();

-- Сколько уже вернули по строке — для проверки «не больше проданного».
CREATE INDEX ix_sale_items_return_of ON sale_items (return_of_item_id) WHERE return_of_item_id IS NOT NULL;

-- Строки чека → движения склада (как раньше) + проверки строк.
CREATE OR REPLACE FUNCTION trg_sale_item_movement() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE n bigint;
BEGIN
  -- Пара — своей фирмы.
  IF EXISTS (SELECT 1 FROM new_items i
             LEFT JOIN product_variants v ON v.id = i.variant_id AND v.tenant_id = i.tenant_id
             WHERE v.id IS NULL) THEN
    RAISE EXCEPTION 'Строка чека ссылается на пару чужой фирмы' USING ERRCODE = 'foreign_key_violation';
  END IF;

  -- Строка продажи не ссылается на строку возврата; строка возврата — строка
  -- исходного чека с той же парой.
  IF EXISTS (SELECT 1 FROM new_items i JOIN sales s ON s.id = i.sale_id
             WHERE (s.type = 'sale' AND i.return_of_item_id IS NOT NULL)
                OR (s.type = 'return' AND NOT EXISTS (
                      SELECT 1 FROM sale_items o
                      WHERE o.id = i.return_of_item_id AND o.sale_id = s.return_of_sale_id AND o.variant_id = i.variant_id))) THEN
    RAISE EXCEPTION 'Строка возврата — не из исходного чека' USING ERRCODE = 'check_violation';
  END IF;

  -- Вернуть можно не больше проданного (с учётом прежних возвратов).
  IF EXISTS (SELECT 1 FROM (SELECT DISTINCT return_of_item_id AS id FROM new_items WHERE return_of_item_id IS NOT NULL) r
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

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 7;
UPDATE tenants SET schema_version = 7 WHERE schema_version < 7;

COMMIT;
