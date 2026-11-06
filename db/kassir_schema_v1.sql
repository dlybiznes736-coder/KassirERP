-- =====================================================================
-- Kassir ERP — схема БД, версия 1 (этап 1: розница, обувь)
-- PostgreSQL 16+. Дальше схема переезжает в EF Core Migrations.
--
-- Восемь принципов ТЗ, как они выполнены здесь:
--  1. id — uuid без DEFAULT: его обязан прислать клиент (касса/телефон).
--     Для серверных строк есть uuid_v7().
--  2. Деньги — numeric(14,2). Количество — numeric(14,3) (под кафе).
--  3. tenant_id во всех таблицах.
--  4. stock_movements — только INSERT; stock меняется триггером уровня
--     оператора: одно обновление на пару за оператор (см. «Триггеры: остатки»).
--  5. sale_items / purchase_items хранят свои price и cost_price.
--  6. Все времена — timestamptz, сервер в UTC.
--  7. Фото — только storage_key (относительный путь).
--  8. RLS + FORCE на всех таблицах с tenant_id; приложение ходит ролью
--     kassir_app (не владелец, не суперпользователь).
-- =====================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- Роль приложения. Пароль — только для разработки.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kassir_app') THEN
    CREATE ROLE kassir_app LOGIN PASSWORD 'kassir_app_dev' NOSUPERUSER NOBYPASSRLS;
  END IF;
END $$;

-- ---------------------------------------------------------------------
-- Служебные функции
-- ---------------------------------------------------------------------

-- Текущий тенант задаётся приложением на каждое соединение/транзакцию:
--   SET app.tenant_id = '<uuid>';   (или SET LOCAL внутри транзакции)
CREATE FUNCTION current_tenant_id() RETURNS uuid
LANGUAGE sql STABLE AS $$
  SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

-- UUID v7 (упорядочен по времени) — для строк, которые создаёт сервер.
-- В PG 18 есть встроенный uuidv7(); до этого — эта реализация.
CREATE FUNCTION uuid_v7() RETURNS uuid
LANGUAGE sql VOLATILE AS $$
  SELECT encode(
    set_bit(set_bit(
      overlay(uuid_send(gen_random_uuid())
              PLACING substring(int8send((extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3)
              FROM 1 FOR 6),
      52, 1), 53, 1), 'hex')::uuid
$$;

CREATE FUNCTION trg_set_updated_at() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END $$;

-- Запрет UPDATE/DELETE для неизменяемых таблиц (журнал, строки чека, оплаты).
-- Обслуживание (перенос тенанта, удаление тестовых данных) делается
-- суперпользователем с SET session_replication_role = replica.
CREATE FUNCTION trg_forbid_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'Таблица % только для добавления: % запрещён', TG_TABLE_NAME, TG_OP
    USING ERRCODE = 'restrict_violation';
END $$;

-- =====================================================================
-- Основа
-- =====================================================================

CREATE TABLE tenants (
  id              uuid PRIMARY KEY,
  name            text NOT NULL,
  tariff          text NOT NULL DEFAULT 'offline' CHECK (tariff IN ('offline', 'online')),
  schema_version  integer NOT NULL DEFAULT 1,     -- читает kassir-migrate
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  deleted_at      timestamptz
);

CREATE TABLE stores (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  name        text NOT NULL,
  address     text,
  timezone    text NOT NULL DEFAULT 'Asia/Bishkek',  -- для отчётов «за день» по местному времени
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  deleted_at  timestamptz
);

CREATE TABLE users (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  store_id       uuid REFERENCES stores(id),        -- NULL = доступ ко всем филиалам
  full_name      text NOT NULL,
  phone          text,
  login          text NOT NULL,
  password_hash  text,
  pin_hash       text,                              -- быстрый вход на кассе
  role           text NOT NULL CHECK (role IN ('owner', 'admin', 'manager', 'cashier', 'storekeeper')),
  is_active      boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  deleted_at     timestamptz,
  CONSTRAINT ux_users_login UNIQUE (tenant_id, login)
);

-- =====================================================================
-- Товары
-- =====================================================================

CREATE TABLE categories (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  parent_id   uuid REFERENCES categories(id),
  name        text NOT NULL,
  sort_order  integer NOT NULL DEFAULT 0,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  deleted_at  timestamptz,
  CHECK (parent_id IS DISTINCT FROM id)
);

-- Модель: без цены и без остатка.
CREATE TABLE products (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  category_id  uuid REFERENCES categories(id),
  name         text NOT NULL,
  brand        text,
  article      text,
  description  text,
  is_active    boolean NOT NULL DEFAULT true,
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  deleted_at   timestamptz
);

-- Конкретная пара: размер + цвет. Здесь цена и остатки.
CREATE TABLE product_variants (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  product_id   uuid NOT NULL REFERENCES products(id),
  size         text NOT NULL,
  color        text NOT NULL,
  barcode      text,
  cost_price   numeric(14,2) NOT NULL DEFAULT 0 CHECK (cost_price >= 0),
  sale_price   numeric(14,2) NOT NULL DEFAULT 0 CHECK (sale_price >= 0),
  is_active    boolean NOT NULL DEFAULT true,
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  deleted_at   timestamptz
);

CREATE TABLE product_photos (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  product_id   uuid NOT NULL REFERENCES products(id),
  variant_id   uuid REFERENCES product_variants(id),  -- NULL = фото модели целиком
  storage_key  text NOT NULL,                          -- {tenant_id}/{product_id}/{uuid}.jpg
  sort_order   integer NOT NULL DEFAULT 0,
  created_at   timestamptz NOT NULL DEFAULT now(),
  CHECK (storage_key NOT LIKE '/%' AND storage_key NOT LIKE '%://%')  -- только относительный путь
);

-- =====================================================================
-- Приход
-- =====================================================================

CREATE TABLE suppliers (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  name        text NOT NULL,
  phone       text,
  inn         text,
  address     text,
  note        text,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  deleted_at  timestamptz
);

CREATE TABLE purchases (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  store_id     uuid NOT NULL REFERENCES stores(id),
  supplier_id  uuid REFERENCES suppliers(id),
  number       text,                                   -- номер накладной поставщика
  doc_date     date NOT NULL DEFAULT current_date,
  status       text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'accepted')),
  total        numeric(14,2) NOT NULL DEFAULT 0,
  created_by   uuid REFERENCES users(id),
  accepted_by  uuid REFERENCES users(id),
  accepted_at  timestamptz,
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  CHECK ((status = 'accepted') = (accepted_at IS NOT NULL))
);

CREATE TABLE purchase_items (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  purchase_id  uuid NOT NULL REFERENCES purchases(id),
  variant_id   uuid NOT NULL REFERENCES product_variants(id),
  qty          numeric(14,3) NOT NULL CHECK (qty > 0),
  cost_price   numeric(14,2) NOT NULL CHECK (cost_price >= 0)  -- фиксируется на момент приёмки
);

-- =====================================================================
-- Склад
-- =====================================================================

-- Журнал движений: источник истины. Только INSERT.
CREATE TABLE stock_movements (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  store_id     uuid NOT NULL REFERENCES stores(id),
  variant_id   uuid NOT NULL REFERENCES product_variants(id),
  qty          numeric(14,3) NOT NULL CHECK (qty <> 0),  -- + приход, − расход
  reason       text NOT NULL CHECK (reason IN (
                 'purchase', 'sale', 'return', 'adjustment', 'writeoff',
                 'transfer_in', 'transfer_out', 'inventory')),
  doc_id       uuid,          -- накладная / чек / акт
  doc_line_id  uuid,          -- строка документа
  cost_price   numeric(14,2),
  occurred_at  timestamptz NOT NULL,                     -- бизнес-время операции
  created_at   timestamptz NOT NULL DEFAULT now(),       -- время записи на сервере
  created_by   uuid
);

-- Кэш текущего остатка. Пишет только триггер, приложение — только читает.
-- Отрицательный остаток разрешён: кассу нельзя блокировать, если приход
-- ещё не оформлен. Минус показывается в отчёте как проблема.
CREATE TABLE stock (
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  store_id    uuid NOT NULL REFERENCES stores(id),
  variant_id  uuid NOT NULL REFERENCES product_variants(id),
  qty         numeric(14,3) NOT NULL DEFAULT 0,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, store_id, variant_id)
);

-- =====================================================================
-- Продажи
-- =====================================================================

CREATE TABLE cash_shifts (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  store_id       uuid NOT NULL REFERENCES stores(id),
  device_id      text NOT NULL,                        -- какая именно касса
  cashier_id     uuid NOT NULL REFERENCES users(id),
  status         text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
  opened_at      timestamptz NOT NULL,                 -- по часам кассы
  closed_at      timestamptz,
  opening_cash   numeric(14,2) NOT NULL DEFAULT 0,
  closing_cash   numeric(14,2),                        -- посчитано кассиром
  expected_cash  numeric(14,2),                        -- посчитано системой
  synced_at      timestamptz NOT NULL DEFAULT now(),
  CHECK ((status = 'closed') = (closed_at IS NOT NULL))
);

CREATE TABLE customers (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  full_name      text,
  phone          text,
  bonus_balance  numeric(14,2) NOT NULL DEFAULT 0,     -- под будущую CRM
  note           text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  deleted_at     timestamptz
);

-- Чек. Возврат — тоже чек, type = 'return', со ссылкой на исходный.
CREATE TABLE sales (
  id                 uuid PRIMARY KEY,                 -- создан на кассе
  tenant_id          uuid NOT NULL REFERENCES tenants(id),
  store_id           uuid NOT NULL REFERENCES stores(id),
  shift_id           uuid NOT NULL REFERENCES cash_shifts(id),
  cashier_id         uuid NOT NULL REFERENCES users(id),
  customer_id        uuid REFERENCES customers(id),
  device_id          text NOT NULL,
  local_number       integer NOT NULL,                 -- номер чека на этой кассе (для людей)
  type               text NOT NULL DEFAULT 'sale' CHECK (type IN ('sale', 'return')),
  return_of_sale_id  uuid REFERENCES sales(id),
  subtotal           numeric(14,2) NOT NULL,
  discount_total     numeric(14,2) NOT NULL DEFAULT 0,
  total              numeric(14,2) NOT NULL,
  created_at_local   timestamptz NOT NULL,             -- время чека по часам кассы
  synced_at          timestamptz NOT NULL DEFAULT now(), -- время приёма сервером
  CHECK ((type = 'return') = (return_of_sale_id IS NOT NULL)),
  CHECK (total = subtotal - discount_total)
);

-- Строки чека: price и cost_price скопированы на момент продажи.
CREATE TABLE sale_items (
  id                 uuid PRIMARY KEY,
  tenant_id          uuid NOT NULL REFERENCES tenants(id),
  sale_id            uuid NOT NULL REFERENCES sales(id),
  variant_id         uuid NOT NULL REFERENCES product_variants(id),
  return_of_item_id  uuid REFERENCES sale_items(id),
  qty                numeric(14,3) NOT NULL CHECK (qty > 0),
  price              numeric(14,2) NOT NULL CHECK (price >= 0),
  cost_price         numeric(14,2) NOT NULL CHECK (cost_price >= 0),
  discount           numeric(14,2) NOT NULL DEFAULT 0 CHECK (discount >= 0),
  total              numeric(14,2) NOT NULL
);

CREATE TABLE payments (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  sale_id     uuid NOT NULL REFERENCES sales(id),
  method      text NOT NULL CHECK (method IN ('cash', 'card', 'transfer', 'other')),
  amount      numeric(14,2) NOT NULL CHECK (amount > 0),
  created_at  timestamptz NOT NULL DEFAULT now()
);

-- =====================================================================
-- Синхронизация
-- =====================================================================

CREATE TABLE sync_outbox (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL REFERENCES tenants(id),
  device_id        text,                               -- кому/от кого
  entity           text NOT NULL,                      -- 'sale', 'product', ...
  entity_id        uuid NOT NULL,
  op               text NOT NULL CHECK (op IN ('upsert', 'delete')),
  payload          jsonb NOT NULL,
  attempts         integer NOT NULL DEFAULT 0,
  last_error       text,
  next_attempt_at  timestamptz NOT NULL DEFAULT now(),
  created_at       timestamptz NOT NULL DEFAULT now(),
  sent_at          timestamptz
);

-- =====================================================================
-- Индексы: по одному на запрос из ТЗ + необходимые уникальные
-- =====================================================================

-- Скан штрихкода (< 5 мс)
CREATE UNIQUE INDEX ux_variants_barcode ON product_variants (tenant_id, barcode)
  WHERE barcode IS NOT NULL AND deleted_at IS NULL;
-- Одна пара размер+цвет на модель
CREATE UNIQUE INDEX ux_variants_product_size_color ON product_variants (product_id, size, color)
  WHERE deleted_at IS NULL;
-- Поиск по названию (< 50 мс на 500 000)
CREATE INDEX ix_products_name_trgm ON products USING gin (name gin_trgm_ops);
-- Товары категории (< 30 мс)
CREATE INDEX ix_products_category ON products (tenant_id, category_id);
-- Отчёт продаж за период (< 300 мс на год)
CREATE INDEX ix_sales_store_date ON sales (tenant_id, store_id, created_at_local);
-- Закрытие смены (< 200 мс)
CREATE INDEX ix_sales_shift ON sales (shift_id);
-- Номер чека уникален в пределах кассы
CREATE UNIQUE INDEX ux_sales_device_number ON sales (tenant_id, device_id, local_number);
-- История движений пары (< 100 мс)
CREATE INDEX ix_movements_variant ON stock_movements (tenant_id, variant_id, occurred_at);
-- Защита от двойного движения по одной строке документа
CREATE UNIQUE INDEX ux_movements_doc_line ON stock_movements (tenant_id, reason, doc_line_id)
  WHERE doc_line_id IS NOT NULL;
-- Одна открытая смена на кассу
CREATE UNIQUE INDEX ux_shifts_open_per_device ON cash_shifts (tenant_id, device_id)
  WHERE status = 'open';
-- Очередь синхронизации (< 10 мс, опрос каждые несколько секунд)
CREATE INDEX ix_outbox_pending ON sync_outbox (next_attempt_at) WHERE sent_at IS NULL;
-- Загрузка документа целиком одним запросом (против N+1)
CREATE INDEX ix_sale_items_sale ON sale_items (sale_id);
CREATE INDEX ix_payments_sale ON payments (sale_id);
CREATE INDEX ix_purchase_items_purchase ON purchase_items (purchase_id);

-- =====================================================================
-- Триггеры: updated_at
-- =====================================================================

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tenants','stores','users','categories','products',
                           'product_variants','suppliers','purchases','customers']
  LOOP
    EXECUTE format('CREATE TRIGGER set_updated_at BEFORE UPDATE ON %I
                    FOR EACH ROW EXECUTE FUNCTION trg_set_updated_at()', t);
  END LOOP;
END $$;

-- =====================================================================
-- Триггеры: остатки
--
-- Все три триггера ниже — уровня ОПЕРАТОРА (FOR EACH STATEMENT, transition
-- tables): строки одного оператора сворачиваются по (тенант, склад, пара),
-- и каждая строка stock сдвигается ровно один раз за оператор.
--
-- ПРАВИЛО ДЛЯ ПАКЕТНЫХ ОПЕРАЦИЙ (импорт, инвентаризация, массовая приёмка,
-- kassir-migrate): движения транзакции пишутся одним INSERT ... SELECT
-- (при необходимости — через временную таблицу), а не циклом по строкам.
-- N отдельных INSERT по одной паре в одной транзакции оставляют N версий
-- одной строки stock, и время на строку растёт линейно (замер T4 в
-- db/bench/bench.sql; T7 — то же самое пачками, без роста).
--
-- kassir-migrate переносит stock_movements и stock как есть, с
-- SET session_replication_role = replica (триггеры не срабатывают), и затем
-- сверяет stock = SUM(stock_movements).
-- =====================================================================

-- Движения оператора → одно обновление stock на пару.
CREATE FUNCTION trg_movement_to_stock() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO stock AS s (tenant_id, store_id, variant_id, qty, updated_at)
  SELECT tenant_id, store_id, variant_id, sum(qty), now()
  FROM new_movements
  GROUP BY tenant_id, store_id, variant_id
  -- Единый порядок блокировок строк stock: параллельные пакеты не
  -- заблокируют друг друга взаимно.
  ORDER BY tenant_id, store_id, variant_id
  ON CONFLICT (tenant_id, store_id, variant_id)
  DO UPDATE SET qty = s.qty + EXCLUDED.qty, updated_at = now();
  RETURN NULL;
END $$;

CREATE TRIGGER movement_to_stock AFTER INSERT ON stock_movements
  REFERENCING NEW TABLE AS new_movements
  FOR EACH STATEMENT EXECUTE FUNCTION trg_movement_to_stock();

CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON stock_movements
  FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();

-- Строки чека → движения (продажа −, возврат +) одним оператором.
-- Чек с кассы приходит одним INSERT всех строк — одно обновление на пару.
CREATE FUNCTION trg_sale_item_movement() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE n bigint;
BEGIN
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), i.tenant_id, s.store_id, i.variant_id,
         CASE WHEN s.type = 'return' THEN i.qty ELSE -i.qty END,
         s.type, i.sale_id, i.id, i.cost_price, s.created_at_local, s.cashier_id
  FROM new_items i
  JOIN sales s ON s.id = i.sale_id AND s.tenant_id = i.tenant_id;

  -- Строка, чей tenant_id не совпал с чеком, выпала бы из JOIN молча.
  GET DIAGNOSTICS n = ROW_COUNT;
  IF n <> (SELECT count(*) FROM new_items) THEN
    RAISE EXCEPTION 'sale_items.tenant_id не совпадает с чеком' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NULL;
END $$;

CREATE TRIGGER sale_item_movement AFTER INSERT ON sale_items
  REFERENCING NEW TABLE AS new_items
  FOR EACH STATEMENT EXECUTE FUNCTION trg_sale_item_movement();

-- Проведённый чек не правится: ошибка исправляется возвратом.
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON sale_items
  FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();
CREATE TRIGGER forbid_change BEFORE UPDATE OR DELETE ON payments
  FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();
CREATE TRIGGER forbid_change BEFORE DELETE ON sales
  FOR EACH ROW EXECUTE FUNCTION trg_forbid_change();

-- Приход: статус только draft → accepted, назад нельзя.
-- Принятую нельзя ни менять, ни удалять; черновик удалять можно.
CREATE FUNCTION trg_purchase_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'accepted' THEN
    RAISE EXCEPTION 'Принятую накладную менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  -- При DELETE записи NEW нет: возврат NEW (NULL) молча отменил бы удаление.
  IF TG_OP = 'DELETE' THEN
    RETURN OLD;
  END IF;
  IF NEW.status = 'accepted' AND NEW.accepted_at IS NULL THEN
    NEW.accepted_at := now();
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER purchase_status_guard BEFORE UPDATE OR DELETE ON purchases
  FOR EACH ROW EXECUTE FUNCTION trg_purchase_status_guard();

-- Остатки меняются только в момент accepted. Все накладные, принятые одним
-- UPDATE, дают один INSERT движений → одно обновление stock на пару.
-- (UPDATE OF status с transition tables в Postgres запрещён, поэтому
-- триггер на любой UPDATE, а переход draft → accepted ищется по OLD/NEW.)
CREATE FUNCTION trg_purchase_accept() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason,
                               doc_id, doc_line_id, cost_price, occurred_at, created_by)
  SELECT uuid_v7(), pi.tenant_id, n.store_id, pi.variant_id, pi.qty, 'purchase',
         n.id, pi.id, pi.cost_price, n.accepted_at, n.accepted_by
  FROM new_purchases n
  JOIN old_purchases o ON o.id = n.id
  JOIN purchase_items pi ON pi.purchase_id = n.id
  WHERE o.status = 'draft' AND n.status = 'accepted';
  RETURN NULL;
END $$;

CREATE TRIGGER purchase_accept AFTER UPDATE ON purchases
  REFERENCING OLD TABLE AS old_purchases NEW TABLE AS new_purchases
  FOR EACH STATEMENT EXECUTE FUNCTION trg_purchase_accept();

-- Строки принятой накладной не меняются.
CREATE FUNCTION trg_purchase_items_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
  SELECT status INTO st FROM purchases
   WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.purchase_id ELSE NEW.purchase_id END;
  IF st = 'accepted' THEN
    RAISE EXCEPTION 'Строки принятой накладной менять нельзя' USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

CREATE TRIGGER purchase_items_guard BEFORE INSERT OR UPDATE OR DELETE ON purchase_items
  FOR EACH ROW EXECUTE FUNCTION trg_purchase_items_guard();

-- =====================================================================
-- Row Level Security
-- =====================================================================

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenants
  USING (id = current_tenant_id()) WITH CHECK (id = current_tenant_id());

DO $$
DECLARE t text;
BEGIN
  FOR t IN
    SELECT c.table_name FROM information_schema.columns c
    JOIN information_schema.tables tb
      ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
    WHERE c.table_schema = 'public' AND c.column_name = 'tenant_id'
      AND tb.table_type = 'BASE TABLE'
  LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %I
                    USING (tenant_id = current_tenant_id())
                    WITH CHECK (tenant_id = current_tenant_id())', t);
  END LOOP;
END $$;

-- =====================================================================
-- Права роли приложения
-- =====================================================================

GRANT USAGE ON SCHEMA public TO kassir_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO kassir_app;
REVOKE INSERT, UPDATE, DELETE ON stock FROM kassir_app;                       -- кэш пишет только триггер
REVOKE UPDATE, DELETE ON stock_movements, sale_items, payments FROM kassir_app;
REVOKE DELETE ON sales FROM kassir_app;

COMMIT;
