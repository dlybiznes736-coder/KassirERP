-- =====================================================================
-- Миграция 0011: синхронизация касс (шаг 6) — забор изменений каталога и цен.
--
-- Касса забирает изменения по курсору (момент изменения, id): товары и пары —
-- по updated_at, цены листа — по price_list_items.updated_at, снятые из листа
-- цены — по журналу price_changes (new_price IS NULL).
--
-- Ловушка курсора по времени: updated_at = now() — это НАЧАЛО транзакции.
-- Транзакция, начатая в 10:00:00 и закоммиченная в 10:00:20 (загрузка
-- прайса — 17 с), кладёт строки «в прошлое»: касса, забравшая изменения в
-- 10:00:10, уже ушла курсором дальше и эти строки не увидит никогда.
-- Поэтому отдаём только строки старше «горизонта» — начала самой старой
-- из идущих сейчас транзакций (sync_horizon). Всё, что старше горизонта,
-- уже закоммичено; более новое касса получит в следующий раз.
--
-- Только добавление: функция и индексы. Таблицы не меняются.
-- =====================================================================
BEGIN;

-- Начало самой старой идущей транзакции клиента в этой базе (или текущий момент).
-- SECURITY DEFINER: роль приложения видит в pg_stat_activity только свои
-- сеансы, а горизонт должен учитывать все (миграции, ручной SQL).
-- Вызывать ОТДЕЛЬНЫМ оператором до чтения изменений: тогда снимок данных
-- (новый у каждого оператора в READ COMMITTED) берётся позже горизонта.
CREATE FUNCTION sync_horizon() RETURNS timestamptz
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT least(clock_timestamp(), min(xact_start))
  FROM pg_stat_activity
  WHERE datname = current_database() AND backend_type = 'client backend' AND xact_start IS NOT NULL
$$;
REVOKE ALL ON FUNCTION sync_horizon() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync_horizon() TO kassir_app;

-- Курсоры: (тенант, момент изменения, id). Без них первая выгрузка 500 000
-- пар и каждый опрос «что нового» читали бы всю таблицу.
CREATE INDEX ix_products_sync ON products (tenant_id, updated_at, id);
CREATE INDEX ix_variants_sync ON product_variants (tenant_id, updated_at, id);
CREATE INDEX ix_price_list_items_sync ON price_list_items (price_list_id, updated_at, variant_id);
CREATE INDEX ix_price_changes_removed ON price_changes (price_list_id, changed_at, variant_id) WHERE new_price IS NULL;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 11;
UPDATE tenants SET schema_version = 11 WHERE schema_version < 11;

COMMIT;
