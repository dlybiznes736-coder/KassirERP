-- =====================================================================
-- Поиск товара по названию: планы и время трёх запросов из bench.sql.
-- Роль kassir_app, app.tenant_id основного тенанта — RLS включён, как в API.
--
--   docker compose exec -T postgres psql -U postgres -d kassir -X -f /db/bench/search_explain.sql
--
-- Форма запроса — та, что сейчас в приложении:
--   есть catalog.search_products (миграция 0003) — через функцию,
--   нет — старый запрос из bench.sql (LIMIT 50 по products ORDER BY name).
-- Для функции внутренний план печатает auto_explain (вложенные операторы).
-- =====================================================================
\set ON_ERROR_STOP on
\pset pager off
\set QUIET on

SELECT to_regprocedure('catalog.search_products(text,integer)') IS NOT NULL AS has_fn,
       (SELECT count(*) FROM products WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001') AS products
\gset

\if :has_fn
  \echo '== Форма: через catalog.search_products (миграция 0003)'
  -- auto_explain можно включить только суперпользователю — до SET ROLE.
  LOAD 'auto_explain';
  SET auto_explain.log_min_duration = 0;
  SET auto_explain.log_nested_statements = on;
  SET auto_explain.log_analyze = on;
  SET auto_explain.log_buffers = on;
  SET auto_explain.log_timing = off;
\else
  \echo '== Форма: старый запрос из bench.sql (до миграции 0003)'
\endif
\echo '   товаров у основного тенанта:' :products

SET ROLE kassir_app;
SELECT set_config('app.tenant_id', 'c0000000-0000-0000-0000-000000000001', false) \gset

CREATE TEMP TABLE q (ord int, label text, query text);
INSERT INTO q VALUES (1, 'редкий', 'кроссовки runner'), (2, 'частый', 'кроссовки'), (3, 'короткий', 'run');

-- Тот же текст, что в приложении (ProductCatalog) / в bench.sql до 0003.
CREATE TEMP TABLE forms (has_fn boolean, sql text);
INSERT INTO forms VALUES
 (true, $s$
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM catalog.search_products(%L, 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
$s$),
 (false, $s$
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM (SELECT id, name, brand FROM products
      WHERE deleted_at IS NULL AND name ILIKE '%%' || %L || '%%'
      ORDER BY name, id LIMIT 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
$s$);

-- Медиана 15 прогонов (планирование + выполнение), как bench.run в bench.sql.
CREATE TEMP TABLE timing (ord int, label text, query text, median_ms numeric, min_ms numeric, max_ms numeric, rows_out bigint);
DO $$
DECLARE r record; j json; t numeric; times numeric[]; tpl text;
BEGIN
  SELECT sql INTO tpl FROM forms WHERE has_fn = (to_regprocedure('catalog.search_products(text,integer)') IS NOT NULL);
  SET LOCAL client_min_messages = warning;   -- auto_explain молчит на этих прогонах
  FOR r IN SELECT * FROM q ORDER BY ord LOOP
    times := '{}';
    FOR i IN 0..15 LOOP
      EXECUTE 'EXPLAIN (ANALYZE, TIMING OFF, FORMAT JSON) ' || format(tpl, r.query) INTO j;
      t := (j->0->>'Planning Time')::numeric + (j->0->>'Execution Time')::numeric;
      IF i > 0 THEN times := times || t; END IF;
    END LOOP;
    INSERT INTO timing SELECT r.ord, r.label, r.query,
      round((SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY x) FROM unnest(times) x)::numeric, 3),
      round((SELECT min(x) FROM unnest(times) x), 3), round((SELECT max(x) FROM unnest(times) x), 3),
      (j->0->'Plan'->>'Actual Rows')::bigint;
  END LOOP;
END $$;

\echo
\echo '== Время (медиана 15 прогонов, планирование + выполнение)'
\pset format aligned
\set QUIET off
SELECT label AS "запрос", query AS "строка", median_ms AS "медиана, мс", min_ms AS "мин", max_ms AS "макс", rows_out AS "строк (пар)"
FROM timing ORDER BY ord;
\set QUIET on

\echo '== Планы (EXPLAIN ANALYZE, BUFFERS)'
SELECT format(sql, 'кроссовки runner') AS q1, format(sql, 'кроссовки') AS q2, format(sql, 'run') AS q3
FROM forms WHERE has_fn = :'has_fn'::boolean \gset

\if :has_fn
SET client_min_messages = log;   -- внутренний план функции (auto_explain)
\endif
\echo '-- 1. редкий: «кроссовки runner»'
EXPLAIN (ANALYZE, BUFFERS) :q1;
\echo '-- 2. частый: «кроссовки»'
EXPLAIN (ANALYZE, BUFFERS) :q2;
\echo '-- 3. короткий: «run»'
EXPLAIN (ANALYZE, BUFFERS) :q3;
