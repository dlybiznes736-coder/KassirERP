-- =====================================================================
-- Kassir ERP — генератор данных для замеров (шаг 3).
--
-- Основной тенант: 50 000 моделей × 10 пар = 500 000 вариантов,
--   3 филиала, 365 дней, 100 000 продаж (+ ~3% возвратов),
--   приход на все пары, 200 000 отправленных + 1 000 ждущих записей outbox.
-- Шумовой тенант: в 10 раз меньше — чтобы индексы и RLS работали
--   на таблицах, где лежат чужие строки.
--
-- Все остатки построены штатно: накладная draft → accepted, строки чека →
-- триггеры → stock_movements → stock. Ничего не пишется в обход триггеров.
--
-- Нужна ПУСТАЯ база со схемой и миграциями:
--   docker compose exec -T postgres bash /db/create_database.sh kassir --recreate
--   docker compose exec -T postgres psql -U postgres -d kassir -X -f /db/bench/generate.sql
-- Больше товаров у основного тенанта (например втрое):
--   ... psql -U postgres -d kassir_x3 -X -v products=150000 -f /db/bench/generate.sql
-- На ноутбуке занимает порядка 5–15 минут (×3 — дольше).
--
-- Только генератор (не миграции) создаёт стенд для замеров API: код фирмы
-- «bench» у основного тенанта и пользователя «bench» (пароль
-- bench-password-1, кладовщик филиала 1). В рабочей базе их нет.
-- =====================================================================
\set ON_ERROR_STOP on
\timing on

\if :{?products}
\else
  \set products 50000
\endif

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM tenants) THEN
    RAISE EXCEPTION 'В базе уже есть тенанты. Генератор работает только на пустой базе: docker compose down -v && docker compose up -d';
  END IF;
END $$;

-- Конец периода данных фиксирован, чтобы прогоны были сравнимы.
\set t_end '2026-11-01 00:00:00+00'
\set days 365

\echo '== Тенант 1: основной (':products' моделей × 10 пар, 100 000 чеков)'
\set tenant 'c0000000-0000-0000-0000-000000000001'
\set tenant_name 'Обувь — основной (замеры)'
\set n_stores 3
\set n_products :products
\set n_sales 100000
\set n_customers 20000
\set bc_prefix 46
\set outbox_sent 200000
\set outbox_pending 1000
\set seed 0.42
\ir _gen_tenant.sql

\echo '== Стенд замеров API: фирма «bench», пользователь «bench»'
UPDATE tenants SET code = 'bench' WHERE id = :'tenant';
-- Хеш PBKDF2-SHA512 (210 000 итераций) пароля «bench-password-1» с фиксированной
-- солью — в формате Pbkdf2PasswordHasher. Пароль известен: только для стенда.
INSERT INTO users (id, tenant_id, store_id, full_name, login, role, password_hash)
SELECT 'b0000000-0000-0000-0000-00000000be01', :'tenant', s.id, 'Замер API', 'bench', 'storekeeper',
       'pbkdf2-sha512$210000$a2Fzc2lyLWJlbmNoLXYxIQ==$FoCyvr0+4QpEQUonMRT4kHQlRU9Fup/90CPFvubUVAU='
FROM stores s WHERE s.tenant_id = :'tenant' AND s.name = 'Филиал 1';

\echo '== Тенант 2: шумовой (50 000 вариантов, 10 000 чеков)'
\set tenant 'd0000000-0000-0000-0000-000000000001'
\set tenant_name 'Обувь — соседний тенант (шум)'
\set n_stores 1
\set n_products 5000
\set n_sales 10000
\set n_customers 2000
\set bc_prefix 47
\set outbox_sent 20000
\set outbox_pending 100
\set seed 0.17
\ir _gen_tenant.sql

\echo '== VACUUM ANALYZE'
VACUUM (ANALYZE);
CHECKPOINT;

\echo '== Проверка: stock = SUM(stock_movements)'
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM stock s
    FULL JOIN (SELECT tenant_id, store_id, variant_id, sum(qty) q
                 FROM stock_movements GROUP BY 1, 2, 3) m
      USING (tenant_id, store_id, variant_id)
    WHERE s.qty IS DISTINCT FROM m.q
  ) THEN
    RAISE EXCEPTION 'stock расходится с журналом движений';
  END IF;
  RAISE NOTICE 'OK  stock совпадает с суммой движений';
END $$;

\echo '== Объёмы'
SELECT t.name AS tenant,
  (SELECT count(*) FROM products p WHERE p.tenant_id = t.id)          AS products,
  (SELECT count(*) FROM product_variants v WHERE v.tenant_id = t.id)  AS variants,
  (SELECT count(*) FROM sales s WHERE s.tenant_id = t.id AND type = 'sale')   AS sales,
  (SELECT count(*) FROM sales s WHERE s.tenant_id = t.id AND type = 'return') AS returns,
  (SELECT count(*) FROM sale_items i WHERE i.tenant_id = t.id)        AS sale_items,
  (SELECT count(*) FROM stock_movements m WHERE m.tenant_id = t.id)   AS movements
FROM tenants t ORDER BY t.id;
