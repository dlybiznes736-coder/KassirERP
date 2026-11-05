-- =====================================================================
-- Kassir ERP — замеры (шаг 4). Запускать после generate.sql.
--
--   docker compose exec -T postgres psql -U postgres -d kassir -X -f /db/bench/bench.sql
--   docker compose cp postgres:/tmp/kassir_bench_report.md db/bench/REPORT.md
--
-- Что делает:
--  1. Каждый запрос из таблицы индексов выполняется ролью kassir_app
--     с app.tenant_id основного тенанта, т.е. с включённым RLS — как в
--     приложении. 1 прогон «первый» + 9 прогонов EXPLAIN (ANALYZE, TIMING OFF);
--     итог = медиана (планирование + выполнение) против цели из ТЗ.
--     Ещё один прогон EXPLAIN (ANALYZE, BUFFERS) — план в отчёт.
--  2. Стоимость триггера movement_to_stock: 10 000 вставок в
--     stock_movements по одной строке, время по пачкам из 1 000.
--     Базовая линия — копия таблицы с теми же данными, индексами, FK
--     и RLS, но без триггера.
--  3. Отчёт одним файлом: /tmp/kassir_bench_report.md внутри контейнера.
--
-- Повторный запуск безопасен: схема bench пересоздаётся. Тест триггеров
-- пишет в отдельный тенант «Замер триггеров» и каждый раз в новый склад.
-- =====================================================================
\set ON_ERROR_STOP on
\set QUIET on
\pset pager off
\set main_tenant 'c0000000-0000-0000-0000-000000000001'
\set trig_tenant 'e0000000-0000-0000-0000-000000000001'

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM tenants WHERE id = 'c0000000-0000-0000-0000-000000000001') THEN
    RAISE EXCEPTION 'Нет данных для замеров: сначала запустите /db/bench/generate.sql';
  END IF;
END $$;

\echo '== Подготовка схемы bench'
DROP SCHEMA IF EXISTS bench CASCADE;
CREATE SCHEMA bench;

CREATE TABLE bench.params (k text PRIMARY KEY, v text NOT NULL);

CREATE TABLE bench.results (
  ord        serial PRIMARY KEY,
  label      text NOT NULL,
  idx        text,
  target_ms  numeric,
  first_ms   numeric,
  median_ms  numeric,
  min_ms     numeric,
  max_ms     numeric,
  runs       int,
  rows_out   bigint,
  plan       text,
  sql        text
);

CREATE TABLE bench.trig (
  ord       serial PRIMARY KEY,
  code      text NOT NULL,
  descr     text NOT NULL,
  batch_no  int NOT NULL,
  rows      int NOT NULL,
  ms        numeric NOT NULL
);

-- Подставляет {ключ} → 'значение' из bench.params.
CREATE FUNCTION bench.sql_of(p_tpl text) RETURNS text
LANGUAGE plpgsql STABLE AS $f$
DECLARE q text := p_tpl; r record;
BEGIN
  FOR r IN SELECT k, v FROM bench.params LOOP
    q := replace(q, '{' || r.k || '}', quote_literal(r.v));
  END LOOP;
  IF q ~ '\{[a-z_]+\}' THEN
    RAISE EXCEPTION 'В запросе остался неподставленный параметр: %', q;
  END IF;
  RETURN q;
END $f$;

-- Замер одного запроса. SECURITY INVOKER: выполняется от kassir_app, RLS включён.
CREATE PROCEDURE bench.run(p_label text, p_idx text, p_target numeric, p_tpl text, p_runs int DEFAULT 9)
LANGUAGE plpgsql AS $f$
DECLARE
  q       text := bench.sql_of(p_tpl);
  j       json;
  t       numeric;
  times   numeric[] := '{}';
  first_t numeric;
  plan    text := '';
  line    text;
BEGIN
  FOR i IN 0..p_runs LOOP
    EXECUTE 'EXPLAIN (ANALYZE, TIMING OFF, FORMAT JSON) ' || q INTO j;
    t := (j->0->>'Planning Time')::numeric + (j->0->>'Execution Time')::numeric;
    IF i = 0 THEN first_t := t; ELSE times := times || t; END IF;
  END LOOP;

  FOR line IN EXECUTE 'EXPLAIN (ANALYZE, BUFFERS) ' || q LOOP
    plan := plan || line || E'\n';
  END LOOP;

  INSERT INTO bench.results (label, idx, target_ms, first_ms, median_ms, min_ms, max_ms,
                             runs, rows_out, plan, sql)
  SELECT p_label, p_idx, p_target, round(first_t, 3),
         round((SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY x) FROM unnest(times) x)::numeric, 3),
         round((SELECT min(x) FROM unnest(times) x), 3),
         round((SELECT max(x) FROM unnest(times) x), 3),
         p_runs, (j->0->'Plan'->>'Actual Rows')::bigint, plan, q;
END $f$;

-- 10 000 вставок в журнал по одной строке, время по пачкам.
--   p_table: 'main' — stock_movements (с триггером), 'clone' — копия без триггера
--   p_mode:  'distinct' — каждая строка в свою пару, 'hot' — все в одну пару
--   p_commit_each: COMMIT после каждой строки (как отдельные чеки)
CREATE PROCEDURE bench.trig_run(p_code text, p_descr text, p_table text, p_mode text,
                                p_commit_each boolean, p_hot int DEFAULT 1,
                                p_n int DEFAULT 10000, p_batch int DEFAULT 1000)
LANGUAGE plpgsql AS $f$
DECLARE
  t_id uuid := current_tenant_id();
  st   uuid := (SELECT v::uuid FROM bench.params WHERE k = 'trig_store');
  vids uuid[];
  vid  uuid;
  t0   timestamptz;
  b    int := 0;
BEGIN
  SELECT array_agg(id ORDER BY id) INTO vids
    FROM (SELECT id FROM product_variants ORDER BY id LIMIT p_n) x;
  IF coalesce(array_length(vids, 1), 0) < p_n THEN
    RAISE EXCEPTION 'В тенанте замера меньше % пар', p_n;
  END IF;

  t0 := clock_timestamp();
  FOR i IN 1..p_n LOOP
    vid := CASE WHEN p_mode = 'hot' THEN vids[p_hot] ELSE vids[i] END;
    IF p_table = 'main' THEN
      INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason, occurred_at)
      VALUES (uuid_v7(), t_id, st, vid, 1, 'adjustment', clock_timestamp());
    ELSE
      INSERT INTO bench.sm_clone (id, tenant_id, store_id, variant_id, qty, reason, occurred_at)
      VALUES (uuid_v7(), t_id, st, vid, 1, 'adjustment', clock_timestamp());
    END IF;
    IF p_commit_each THEN COMMIT; END IF;

    IF i % p_batch = 0 THEN
      b := b + 1;
      INSERT INTO bench.trig (code, descr, batch_no, rows, ms)
      VALUES (p_code, p_descr, b, p_batch, extract(epoch FROM clock_timestamp() - t0) * 1000);
      IF p_commit_each THEN COMMIT; END IF;
      t0 := clock_timestamp();
    END IF;
  END LOOP;
END $f$;

-- ---------------------------------------------------------------------
-- Параметры запросов: худшие случаи по данным основного тенанта
-- ---------------------------------------------------------------------
\echo '== Выбор параметров'
INSERT INTO bench.params VALUES
  ('q_rare',     'кроссовки runner'),   -- ~0.17% моделей
  ('q_freq',     'кроссовки'),          -- ~10% моделей
  ('q_short',    'run'),                -- 3 символа = один триграм
  ('bc_missing', '0000000000000');

INSERT INTO bench.params
SELECT 'store', store_id::text FROM sales WHERE tenant_id = :'main_tenant'
GROUP BY store_id ORDER BY count(*) DESC LIMIT 1;

INSERT INTO bench.params
SELECT 'bc', barcode FROM product_variants
WHERE tenant_id = :'main_tenant' AND barcode IS NOT NULL AND deleted_at IS NULL
ORDER BY barcode OFFSET 250000 LIMIT 1;

INSERT INTO bench.params
SELECT 'variant_typ', id::text FROM product_variants
WHERE tenant_id = :'main_tenant' AND barcode = (SELECT v FROM bench.params WHERE k = 'bc');

INSERT INTO bench.params
SELECT 'cat', category_id::text FROM products WHERE tenant_id = :'main_tenant'
GROUP BY category_id ORDER BY count(*) DESC LIMIT 1;

INSERT INTO bench.params
SELECT 'period_to', (date_trunc('day', max(created_at_local)) + interval '1 day')::text
FROM sales WHERE tenant_id = :'main_tenant';
INSERT INTO bench.params
SELECT 'period_from', ((SELECT v FROM bench.params WHERE k = 'period_to')::timestamptz - interval '1 year')::text;

INSERT INTO bench.params
SELECT 'shift', shift_id::text FROM sales WHERE tenant_id = :'main_tenant'
GROUP BY shift_id ORDER BY count(*) DESC, shift_id LIMIT 1;

INSERT INTO bench.params
SELECT 'variant_hot', variant_id::text FROM stock_movements WHERE tenant_id = :'main_tenant'
GROUP BY variant_id ORDER BY count(*) DESC, variant_id LIMIT 1;

INSERT INTO bench.params
SELECT 'sale_doc', sale_id::text FROM sale_items WHERE tenant_id = :'main_tenant'
GROUP BY sale_id ORDER BY count(*) DESC, sale_id LIMIT 1;

INSERT INTO bench.params
SELECT 'purchase_doc', purchase_id::text FROM purchase_items WHERE tenant_id = :'main_tenant'
GROUP BY purchase_id ORDER BY count(*) DESC, purchase_id LIMIT 1;

-- ---------------------------------------------------------------------
-- Тенант и копия журнала для теста триггеров
-- ---------------------------------------------------------------------
\echo '== Подготовка теста триггеров (копия stock_movements)'
DO $$
DECLARE t uuid := 'e0000000-0000-0000-0000-000000000001'; p uuid;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM tenants WHERE id = t) THEN
    INSERT INTO tenants (id, name) VALUES (t, 'Замер триггеров');
    p := uuid_v7();
    INSERT INTO products (id, tenant_id, name) VALUES (p, t, 'Товар для замера');
    INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode)
    SELECT uuid_v7(), t, p, n::text, 'тест', 'E' || lpad(n::text, 12, '0')
    FROM generate_series(1, 10000) n;
  END IF;
END $$;

WITH s AS (
  INSERT INTO stores (id, tenant_id, name)
  VALUES (uuid_v7(), :'trig_tenant', 'Склад замера ' || to_char(now(), 'YYYY-MM-DD HH24:MI:SS'))
  RETURNING id
)
INSERT INTO bench.params SELECT 'trig_store', id::text FROM s;

-- Та же структура, те же данные и индексы, те же FK и RLS — только без триггера.
CREATE TABLE bench.sm_clone (LIKE public.stock_movements INCLUDING DEFAULTS INCLUDING CONSTRAINTS);
INSERT INTO bench.sm_clone SELECT * FROM public.stock_movements;
ALTER TABLE bench.sm_clone ADD PRIMARY KEY (id);
CREATE INDEX ON bench.sm_clone (tenant_id, variant_id, occurred_at);
CREATE UNIQUE INDEX ON bench.sm_clone (tenant_id, reason, doc_line_id) WHERE doc_line_id IS NOT NULL;
ALTER TABLE bench.sm_clone
  ADD FOREIGN KEY (tenant_id)  REFERENCES tenants(id),
  ADD FOREIGN KEY (store_id)   REFERENCES stores(id),
  ADD FOREIGN KEY (variant_id) REFERENCES product_variants(id);
ALTER TABLE bench.sm_clone ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON bench.sm_clone
  USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());
VACUUM ANALYZE bench.sm_clone;

GRANT USAGE ON SCHEMA bench TO kassir_app;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA bench TO kassir_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA bench TO kassir_app;

-- =====================================================================
-- 1. Запросы — от имени приложения
-- =====================================================================
\echo '== Замеры запросов (роль kassir_app, RLS включён)'
SET app.tenant_id = :'main_tenant';
SET ROLE kassir_app;

CALL bench.run('Скан штрихкода — пара есть', 'ux_variants_barcode', 5, $q$
SELECT v.id, p.name, p.brand, v.size, v.color, v.sale_price, coalesce(s.qty, 0) AS qty
FROM product_variants v
JOIN products p ON p.id = v.product_id
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = {store} AND s.variant_id = v.id
WHERE v.barcode = {bc} AND v.deleted_at IS NULL
$q$);

CALL bench.run('Скан штрихкода — нет в базе', 'ux_variants_barcode', 5, $q$
SELECT v.id, p.name, p.brand, v.size, v.color, v.sale_price, coalesce(s.qty, 0) AS qty
FROM product_variants v
JOIN products p ON p.id = v.product_id
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = {store} AND s.variant_id = v.id
WHERE v.barcode = {bc_missing} AND v.deleted_at IS NULL
$q$);

CALL bench.run('Поиск по названию — редкий запрос «кроссовки runner»', 'ix_products_name_trgm', 50, $q$
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM products p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
WHERE p.deleted_at IS NULL AND p.name ILIKE '%' || {q_rare} || '%'
ORDER BY p.name, p.id, v.size, v.color
LIMIT 50
$q$);

CALL bench.run('Поиск по названию — частый запрос «кроссовки»', 'ix_products_name_trgm', 50, $q$
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM products p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
WHERE p.deleted_at IS NULL AND p.name ILIKE '%' || {q_freq} || '%'
ORDER BY p.name, p.id, v.size, v.color
LIMIT 50
$q$);

CALL bench.run('Поиск по названию — короткий запрос «run»', 'ix_products_name_trgm', 50, $q$
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM products p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
WHERE p.deleted_at IS NULL AND p.name ILIKE '%' || {q_short} || '%'
ORDER BY p.name, p.id, v.size, v.color
LIMIT 50
$q$);

CALL bench.run('Товары категории — самая большая, первая страница', 'ix_products_category', 30, $q$
SELECT p.id, p.name, p.brand, p.article, pr.min_price, pr.max_price, pr.variants
FROM (SELECT id, name, brand, article FROM products
      WHERE category_id = {cat} AND deleted_at IS NULL
      ORDER BY name, id LIMIT 50) p
CROSS JOIN LATERAL (
  SELECT min(sale_price) AS min_price, max(sale_price) AS max_price, count(*) AS variants
  FROM product_variants v WHERE v.product_id = p.id AND v.deleted_at IS NULL) pr
ORDER BY p.name, p.id
$q$);

CALL bench.run('Отчёт за год — выручка по дням, один филиал', 'ix_sales_store_date', 300, $q$
SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       count(*) FILTER (WHERE s.type = 'sale')      AS checks,
       sum(s.total) FILTER (WHERE s.type = 'sale')   AS revenue,
       sum(s.total) FILTER (WHERE s.type = 'return') AS returns
FROM sales s
WHERE s.store_id = {store}
  AND s.created_at_local >= {period_from} AND s.created_at_local < {period_to}
GROUP BY 1 ORDER BY 1
$q$);

CALL bench.run('Отчёт за год — выручка, себестоимость, прибыль по дням, один филиал', 'ix_sales_store_date', 300, $q$
SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       sum(CASE WHEN s.type = 'sale' THEN i.total ELSE -i.total END)                       AS revenue,
       sum(CASE WHEN s.type = 'sale' THEN i.qty * i.cost_price ELSE -i.qty * i.cost_price END) AS cost,
       sum(CASE WHEN s.type = 'sale' THEN 1 ELSE -1 END * (i.total - i.qty * i.cost_price)) AS profit
FROM sales s
JOIN sale_items i ON i.sale_id = s.id
WHERE s.store_id = {store}
  AND s.created_at_local >= {period_from} AND s.created_at_local < {period_to}
GROUP BY 1 ORDER BY 1
$q$);

CALL bench.run('Отчёт за год — выручка, себестоимость, прибыль по дням, все филиалы', 'ix_sales_store_date', 300, $q$
SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       sum(CASE WHEN s.type = 'sale' THEN i.total ELSE -i.total END)                       AS revenue,
       sum(CASE WHEN s.type = 'sale' THEN i.qty * i.cost_price ELSE -i.qty * i.cost_price END) AS cost,
       sum(CASE WHEN s.type = 'sale' THEN 1 ELSE -1 END * (i.total - i.qty * i.cost_price)) AS profit
FROM sales s
JOIN sale_items i ON i.sale_id = s.id
WHERE s.created_at_local >= {period_from} AND s.created_at_local < {period_to}
GROUP BY 1 ORDER BY 1
$q$);

CALL bench.run('Закрытие смены — самая загруженная смена', 'ix_sales_shift', 200, $q$
SELECT p.method,
       count(DISTINCT s.id) AS checks,
       sum(CASE WHEN s.type = 'sale' THEN p.amount ELSE -p.amount END) AS amount
FROM sales s
JOIN payments p ON p.sale_id = s.id
WHERE s.shift_id = {shift}
GROUP BY p.method
$q$);

CALL bench.run('История движений — самая ходовая пара, вся история с остатком', 'ix_movements_variant', 100, $q$
SELECT m.occurred_at, m.reason, m.qty, m.store_id, m.doc_id,
       sum(m.qty) OVER (PARTITION BY m.store_id ORDER BY m.occurred_at, m.id) AS balance
FROM stock_movements m
WHERE m.variant_id = {variant_hot}
ORDER BY m.occurred_at DESC, m.id DESC
$q$);

CALL bench.run('История движений — обычная пара', 'ix_movements_variant', 100, $q$
SELECT m.occurred_at, m.reason, m.qty, m.store_id, m.doc_id,
       sum(m.qty) OVER (PARTITION BY m.store_id ORDER BY m.occurred_at, m.id) AS balance
FROM stock_movements m
WHERE m.variant_id = {variant_typ}
ORDER BY m.occurred_at DESC, m.id DESC
$q$);

CALL bench.run('Очередь синхронизации — взять пачку 100', 'ix_outbox_pending', 10, $q$
SELECT id, entity, entity_id, op, payload, attempts
FROM sync_outbox
WHERE sent_at IS NULL AND next_attempt_at <= now()
ORDER BY next_attempt_at
LIMIT 100
FOR UPDATE SKIP LOCKED
$q$);

CALL bench.run('Чек целиком — строки и оплаты одним запросом', 'ix_sale_items_sale', NULL, $q$
SELECT s.*,
       (SELECT json_agg(i ORDER BY i.id) FROM sale_items i WHERE i.sale_id = s.id) AS items,
       (SELECT json_agg(p ORDER BY p.id) FROM payments p WHERE p.sale_id = s.id)   AS payments
FROM sales s
WHERE s.id = {sale_doc}
$q$);

CALL bench.run('Накладная целиком — 100 строк одним запросом', 'ix_purchase_items_purchase', NULL, $q$
SELECT pu.*,
       (SELECT json_agg(pi ORDER BY pi.id) FROM purchase_items pi WHERE pi.purchase_id = pu.id) AS items
FROM purchases pu
WHERE pu.id = {purchase_doc}
$q$);

-- =====================================================================
-- 2. Стоимость триггеров
-- =====================================================================
\echo '== Тест триггеров: 6 сценариев по 10 000 строк'
SET app.tenant_id = :'trig_tenant';

CALL bench.trig_run('T1', 'Без триггера (копия журнала): 10 000 разных пар, одна транзакция', 'clone', 'distinct', false);
CALL bench.trig_run('T2', 'С триггером: 10 000 разных пар, строк в stock ещё нет (INSERT), одна транзакция', 'main', 'distinct', false);
CALL bench.trig_run('T3', 'С триггером: те же 10 000 пар, строки в stock есть (UPDATE), одна транзакция', 'main', 'distinct', false);
CALL bench.trig_run('T4', 'С триггером: одна пара 10 000 раз, одна транзакция', 'main', 'hot', false, 1);

-- Коммит на каждую строку: fsync отключён, чтобы мерить триггер, а не диск.
SET synchronous_commit = off;
CALL bench.trig_run('T5', 'Без триггера (копия журнала): одна пара 10 000 раз, COMMIT на каждую строку', 'clone', 'hot', true, 2);
CALL bench.trig_run('T6', 'С триггером: одна пара 10 000 раз, COMMIT на каждую строку', 'main', 'hot', true, 3);
RESET synchronous_commit;

RESET ROLE;
RESET app.tenant_id;

-- =====================================================================
-- 3. Отчёт
-- =====================================================================
\echo '== Сборка отчёта'
\pset format unaligned
\pset tuples_only on
\pset footer off
\o /tmp/kassir_bench_report.md

-- Заголовок и окружение
SELECT format(E'# Kassir ERP — замеры БД\n\n'
  'Дата прогона: %s (Бишкек)  \n'
  'Сервер: %s  \n'
  'shared_buffers = %s, work_mem = %s, effective_cache_size = %s, jit = %s, random_page_cost = %s\n\n'
  'Как мерили: каждый запрос выполнен ролью `kassir_app` с `app.tenant_id` основного тенанта — '
  'RLS включён, как в приложении. 1 первый прогон + %s прогонов `EXPLAIN (ANALYZE, TIMING OFF)`; '
  '**итог = медиана (планирование + выполнение)**, сравнивается с целью из ТЗ. '
  'Сеть и передача результата клиенту в замер не входят. '
  'План ниже — отдельный прогон `EXPLAIN (ANALYZE, BUFFERS)`.\n',
  to_char(now() AT TIME ZONE 'Asia/Bishkek', 'YYYY-MM-DD HH24:MI'),
  version(),
  current_setting('shared_buffers'), current_setting('work_mem'),
  current_setting('effective_cache_size'), current_setting('jit'),
  current_setting('random_page_cost'),
  (SELECT max(runs) FROM bench.results));

-- Объёмы
SELECT E'## Объёмы данных\n\n'
  '| Таблица | Основной тенант | Всего в таблице | Размер с индексами |\n'
  '|---|--:|--:|--:|\n' ||
  string_agg(format('| %s | %s | %s | %s |', t, main, total,
                    pg_size_pretty(pg_total_relation_size(t::regclass))), E'\n' ORDER BY o)
  || E'\n'
FROM (VALUES
  (1,  'products',         (SELECT count(*) FROM products WHERE tenant_id = :'main_tenant'),         (SELECT count(*) FROM products)),
  (2,  'product_variants', (SELECT count(*) FROM product_variants WHERE tenant_id = :'main_tenant'), (SELECT count(*) FROM product_variants)),
  (3,  'sales',            (SELECT count(*) FROM sales WHERE tenant_id = :'main_tenant'),            (SELECT count(*) FROM sales)),
  (4,  'sale_items',       (SELECT count(*) FROM sale_items WHERE tenant_id = :'main_tenant'),       (SELECT count(*) FROM sale_items)),
  (5,  'payments',         (SELECT count(*) FROM payments WHERE tenant_id = :'main_tenant'),         (SELECT count(*) FROM payments)),
  (6,  'cash_shifts',      (SELECT count(*) FROM cash_shifts WHERE tenant_id = :'main_tenant'),      (SELECT count(*) FROM cash_shifts)),
  (7,  'purchase_items',   (SELECT count(*) FROM purchase_items WHERE tenant_id = :'main_tenant'),   (SELECT count(*) FROM purchase_items)),
  (8,  'stock_movements',  (SELECT count(*) FROM stock_movements WHERE tenant_id = :'main_tenant'),  (SELECT count(*) FROM stock_movements)),
  (9,  'stock',            (SELECT count(*) FROM stock WHERE tenant_id = :'main_tenant'),            (SELECT count(*) FROM stock)),
  (10, 'sync_outbox',      (SELECT count(*) FROM sync_outbox WHERE tenant_id = :'main_tenant'),      (SELECT count(*) FROM sync_outbox))
) x(o, t, main, total);

SELECT format(E'Чеков основного тенанта: %s продаж + %s возвратов. '
              'Строк outbox, ждущих отправки: %s. '
              'Целостность `stock = SUM(stock_movements)`: **%s**.\n',
  (SELECT count(*) FROM sales WHERE tenant_id = :'main_tenant' AND type = 'sale'),
  (SELECT count(*) FROM sales WHERE tenant_id = :'main_tenant' AND type = 'return'),
  (SELECT count(*) FROM sync_outbox WHERE tenant_id = :'main_tenant' AND sent_at IS NULL),
  CASE WHEN EXISTS (
    SELECT 1 FROM stock s
    FULL JOIN (SELECT tenant_id, store_id, variant_id, sum(qty) q
                 FROM stock_movements GROUP BY 1, 2, 3) m USING (tenant_id, store_id, variant_id)
    WHERE s.qty IS DISTINCT FROM m.q) THEN 'РАСХОДИТСЯ' ELSE 'совпадает' END);

-- Сводная таблица
SELECT E'## Итог по запросам\n\n'
  '| # | Запрос | Индекс | Индекс в плане | Цель ТЗ | Медиана | Первый прогон | Строк | Итог |\n'
  '|--:|---|---|:-:|--:|--:|--:|--:|---|\n' ||
  string_agg(format('| %s | %s | `%s` | %s | %s | %s мс | %s мс | %s | %s |',
    ord, label, idx,
    CASE WHEN position(idx IN plan) > 0 THEN 'да' ELSE '**нет**' END,
    coalesce('< ' || target_ms || ' мс', '—'),
    median_ms, first_ms, rows_out,
    CASE WHEN target_ms IS NULL THEN 'цели в ТЗ нет'
         WHEN median_ms < target_ms THEN '✅ пройдено'
         ELSE '❌ НЕ пройдено' END), E'\n' ORDER BY ord)
  || E'\n\n' ||
  format('Пройдено %s из %s запросов с целью в ТЗ.',
    count(*) FILTER (WHERE target_ms IS NOT NULL AND median_ms < target_ms),
    count(*) FILTER (WHERE target_ms IS NOT NULL)) || E'\n'
FROM bench.results;

-- Подробно по каждому запросу
SELECT E'## Запросы подробно\n\n' ||
  string_agg(format(E'### %s. %s\n\n'
    'Цель ТЗ: %s · медиана **%s мс** (%s прогонов) · первый прогон %s мс · мин %s / макс %s мс · индекс `%s`\n\n'
    '**Итог: %s**\n\n'
    '```sql\n%s\n```\n\n'
    '```\n%s```\n',
    ord, label,
    coalesce('< ' || target_ms || ' мс', 'нет'),
    median_ms, runs, first_ms, min_ms, max_ms, idx,
    CASE WHEN target_ms IS NULL THEN 'цели в ТЗ нет, для справки'
         WHEN median_ms < target_ms THEN 'ПРОЙДЕНО'
         ELSE 'НЕ ПРОЙДЕНО' END,
    btrim(sql, E'\n'), plan), E'\n' ORDER BY ord)
FROM bench.results;

-- Триггеры: сводка
SELECT E'## Стоимость триггеров\n\n'
  'Вставка 10 000 строк в `stock_movements`, каждая — отдельный `INSERT` (как строка чека). '
  'Время снято по пачкам из 1 000 строк. Рост = (среднее за последние 3 пачки) / (среднее за первые 3). '
  'База для сравнения — копия журнала с теми же данными, индексами, FK и RLS, но без триггера `movement_to_stock`. '
  'В T5/T6 `synchronous_commit = off`, чтобы мерить триггер, а не fsync диска.\n\n'
  '| Код | Сценарий | Строк | Всего, мс | мс/строку | Первые 3 пачки, мс/строку | Последние 3 пачки, мс/строку | Рост | Время растёт? |\n'
  '|---|---|--:|--:|--:|--:|--:|--:|---|\n' ||
  string_agg(format('| %s | %s | %s | %s | %s | %s | %s | ×%s | %s |',
    code, descr, n, round(total), round(total / n, 4), round(f, 4), round(l, 4), round(l / f, 2),
    CASE WHEN l / f > 1.5 THEN '**да**' ELSE 'нет' END), E'\n' ORDER BY o) || E'\n'
FROM (
  SELECT code, min(descr) AS descr, min(ord) AS o, sum(rows) AS n, sum(ms) AS total,
         avg(ms / rows) FILTER (WHERE batch_no <= 3)      AS f,
         avg(ms / rows) FILTER (WHERE batch_no > mb - 3)  AS l
  FROM (SELECT *, max(batch_no) OVER (PARTITION BY code) AS mb FROM bench.trig) b
  GROUP BY code
) s;

-- Триггеры: цена триггера в мс на строку
SELECT format(E'Цена триггера `movement_to_stock` (разница с копией без триггера):\n\n'
  '- новая строка остатка (INSERT в stock): **%s мс/строку** (T2 − T1)\n'
  '- существующая строка остатка (UPDATE stock): **%s мс/строку** (T3 − T1)\n'
  '- одна и та же пара, коммит на строку: **%s мс/строку** (T6 − T5)\n'
  '- одна и та же пара 10 000 раз в одной транзакции: **%s мс/строку** (T4 − T1)\n',
  round(t2 - t1, 4), round(t3 - t1, 4), round(t6 - t5, 4), round(t4 - t1, 4))
FROM (
  SELECT max(r) FILTER (WHERE code = 'T1') t1, max(r) FILTER (WHERE code = 'T2') t2,
         max(r) FILTER (WHERE code = 'T3') t3, max(r) FILTER (WHERE code = 'T4') t4,
         max(r) FILTER (WHERE code = 'T5') t5, max(r) FILTER (WHERE code = 'T6') t6
  FROM (SELECT code, sum(ms) / sum(rows) r FROM bench.trig GROUP BY code) x
) y;

-- Триггеры: по пачкам
SELECT E'### По пачкам, мс на строку\n\n'
  '| Код | ' || (SELECT string_agg(b::text, ' | ' ORDER BY b) FROM generate_series(1, (SELECT max(batch_no) FROM bench.trig)) b) || E' |\n'
  '|---|' || repeat('--:|', (SELECT max(batch_no) FROM bench.trig)) || E'\n' ||
  string_agg(line, E'\n' ORDER BY code) || E'\n'
FROM (
  SELECT code, '| ' || code || ' | ' || string_agg(round(ms / rows, 4)::text, ' | ' ORDER BY batch_no) || ' |' AS line
  FROM bench.trig GROUP BY code
) x;

-- Параметры, на которых мерили
SELECT E'## Параметры замера\n\n| Ключ | Значение |\n|---|---|\n' ||
  string_agg(format('| %s | `%s` |', k, v), E'\n' ORDER BY k) || E'\n'
FROM bench.params;

\o
\pset format aligned
\pset tuples_only off

DROP TABLE bench.sm_clone;
\echo 'Готово. Отчёт: /tmp/kassir_bench_report.md'
\echo 'Забрать: docker compose cp postgres:/tmp/kassir_bench_report.md db/bench/REPORT.md'
