-- =====================================================================
-- Миграция 0003: поиск товара по названию через индекс.
--   docker compose exec -T postgres psql -U postgres -d kassir -v ON_ERROR_STOP=1 -f /db/migrations/0003_product_search.sql
--
-- Почему старый GIN-индекс ix_products_name_trgm не работал. Он есть с
-- версии 1, но под RLS не используется никогда: оператор ILIKE (texticlike)
-- не помечен LEAKPROOF, а Postgres не применяет такие условия через индекс
-- раньше политики RLS (tenant_id = current_tenant_id()). Получался Seq Scan
-- по всем товарам базы — время росло вместе с числом товаров. Составной
-- индекс с tenant_id упирается в то же самое.
--
-- Решение — узкая функция SECURITY DEFINER только для поиска: внутри неё RLS
-- не действует, фильтр по тенанту записан явно (tenant_id = current_tenant_id(),
-- то есть тенант по-прежнему только из TenantDb). Тенант не задан — пусто.
--
-- Поиск возвращает до p_limit ЛЮБЫХ совпадений, а не первые по алфавиту:
-- «первые 50 по алфавиту» требует отсортировать все совпадения, и частый
-- запрос («кроссовки» — 10 % товаров) снова растёт с числом товаров.
-- Порядок для показа (по названию) задаёт вызывающий запрос.
-- =====================================================================
BEGIN;

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gin;   -- uuid в GIN-индексе рядом с триграммами

-- Старый индекс: только name, под RLS не используется.
DROP INDEX IF EXISTS ix_products_name_trgm;

-- tenant_id в индексе: в облаке триграммы «кро», «рос»… есть у товаров всех
-- фирм, без tenant_id индекс отдавал бы совпадения всей базы.
-- На большой живой таблице — CREATE INDEX CONCURRENTLY вне транзакции.
CREATE INDEX ix_products_search ON products
  USING gin (tenant_id, name gin_trgm_ops)
  WHERE deleted_at IS NULL;

CREATE SCHEMA catalog;
REVOKE ALL ON SCHEMA catalog FROM PUBLIC;
GRANT USAGE ON SCHEMA catalog TO kassir_app;

-- p_query — строка как ввёл пользователь; % _ \ экранируются здесь, ищутся буквально.
-- Меньше 3 символов — пусто: из двух букв не получается ни одной полной
-- триграммы, индекс не помогает, и запрос без совпадений проходит всю фирму
-- (на 150 000 товаров — 120 мс и растёт линейно). API требует от 3 символов.
-- EXECUTE ... USING: план строится под конкретное значение при каждом вызове.
-- enable_seqscan = off: при частом шаблоне планировщик иначе может выбрать
-- Seq Scan с LIMIT, который на редком шаблоне проходит всю таблицу.
CREATE FUNCTION catalog.search_products(p_query text, p_limit int)
RETURNS TABLE (id uuid, name text, brand text, article text)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = public, pg_temp
SET enable_seqscan = off
AS $$
DECLARE
  t uuid := current_tenant_id();
  q text := btrim(coalesce(p_query, ''));
BEGIN
  IF t IS NULL OR char_length(q) < 3 THEN
    RETURN;
  END IF;

  RETURN QUERY EXECUTE
    'SELECT p.id, p.name, p.brand, p.article
       FROM products p
      WHERE p.tenant_id = $1 AND p.deleted_at IS NULL
        AND p.name ILIKE ''%'' || $2 || ''%''
      LIMIT $3'
    USING t,
          replace(replace(replace(q, '\', '\\'), '%', '\%'), '_', '\_'),
          least(greatest(coalesce(p_limit, 50), 1), 100);
END $$;

REVOKE ALL ON FUNCTION catalog.search_products(text, int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION catalog.search_products(text, int) TO kassir_app;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 3;
UPDATE tenants SET schema_version = 3 WHERE schema_version < 3;

COMMIT;
