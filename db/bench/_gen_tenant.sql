-- Генерация одного тенанта. Вызывается из generate.sql через \ir,
-- параметры приходят переменными psql: tenant, tenant_name, n_stores,
-- n_products, n_sales, n_customers, days, t_end, bc_prefix,
-- outbox_sent, outbox_pending, seed.
-- Только обычный SQL (без DO-блоков): psql не подставляет переменные
-- внутрь $$-строк.

BEGIN;
SELECT setseed(:seed);

INSERT INTO tenants (id, name, tariff) VALUES (:'tenant', :'tenant_name', 'online');

-- ---------------------------------------------------------------------
-- Филиалы, пользователи, поставщики, покупатели
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_stores AS
SELECT n, uuid_v7() AS id FROM generate_series(1, :n_stores) n;

INSERT INTO stores (id, tenant_id, name, address)
SELECT id, :'tenant', 'Филиал ' || n, 'ул. Тестовая, ' || n FROM g_stores;

CREATE TEMP TABLE g_owner AS SELECT uuid_v7() AS id;
INSERT INTO users (id, tenant_id, full_name, login, role)
SELECT id, :'tenant', 'Владелец', 'owner', 'owner' FROM g_owner;

CREATE TEMP TABLE g_users AS
SELECT s.n AS store_n, s.id AS store_id, k, uuid_v7() AS id
FROM g_stores s CROSS JOIN generate_series(1, 2) k;

INSERT INTO users (id, tenant_id, store_id, full_name, login, role)
SELECT id, :'tenant', store_id, 'Кассир ' || store_n || '-' || k,
       'cashier' || store_n || '_' || k, 'cashier'
FROM g_users;

CREATE TEMP TABLE g_suppliers AS SELECT n, uuid_v7() AS id FROM generate_series(1, 30) n;
INSERT INTO suppliers (id, tenant_id, name, phone)
SELECT id, :'tenant', 'Поставщик ' || n, '+996555' || lpad(n::text, 6, '0') FROM g_suppliers;

CREATE TEMP TABLE g_customers AS SELECT n, uuid_v7() AS id FROM generate_series(1, :n_customers) n;
INSERT INTO customers (id, tenant_id, full_name, phone)
SELECT id, :'tenant', 'Покупатель ' || n, '+996' || lpad((700000000 + n)::text, 9, '0')
FROM g_customers;

-- ---------------------------------------------------------------------
-- Категории: 8 верхних × 5 подкатегорий
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_cat_top AS
SELECT n, uuid_v7() AS id,
       (ARRAY['Мужская обувь','Женская обувь','Детская обувь','Спорт',
              'Сезонная','Домашняя','Аксессуары','Распродажа'])[n] AS name
FROM generate_series(1, 8) n;

INSERT INTO categories (id, tenant_id, name, sort_order)
SELECT id, :'tenant', name, n FROM g_cat_top;

CREATE TEMP TABLE g_cat AS
SELECT row_number() OVER (ORDER BY t.n, k) AS n, uuid_v7() AS id, t.id AS parent_id,
       t.name || ' / группа ' || k AS name
FROM g_cat_top t CROSS JOIN generate_series(1, 5) k;

INSERT INTO categories (id, tenant_id, parent_id, name, sort_order)
SELECT id, :'tenant', parent_id, name, n FROM g_cat;

-- ---------------------------------------------------------------------
-- Модели. Категории неравномерные: первая получает ~16% товаров —
-- это худший случай для запроса «товары категории».
-- Название: «<тип> <модель> <материал>», 10 × 60 × 8 = 4 800 вариантов.
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_products AS
SELECT n, uuid_v7() AS id,
       1 + floor(40 * random() ^ 2)::int    AS cat_n,
       1 + floor(10 * random())::int        AS type_n,
       1 + floor(60 * random())::int        AS model_n,
       1 + floor(8 * random())::int         AS adj_n,
       1 + floor(10 * random())::int        AS brand_n,
       36 + floor(6 * random())::int        AS size0,
       1 + floor(8 * random())::int         AS col1,
       (round((1500 + random() * 8500) / 10) * 10)::numeric(14,2) AS base_price
FROM generate_series(1, :n_products) n;

INSERT INTO products (id, tenant_id, category_id, name, brand, article, created_at, updated_at)
SELECT p.id, :'tenant', c.id,
       (ARRAY['Кроссовки','Ботинки','Туфли','Кеды','Сапоги',
              'Сандалии','Мокасины','Лоферы','Слипоны','Угги'])[p.type_n]
       || ' ' ||
       (ARRAY['Runner','Classic','Urban','Trail','Street','Comfort','Sport','Retro','Nova','Atlas',
              'Vector','Pulse','Breeze','Summit','Metro','Glide','Swift','Orbit','Storm','Flex',
              'Prime','Zen','Apex','Echo','Fusion','Nomad','Polar','Rapid','Sierra','Terra',
              'Volt','Wave','Alpine','Bolt','Cosmo','Delta','Ember','Falcon','Granite','Harbor',
              'Ivory','Jet','Kodiak','Lunar','Mesa','Nimbus','Onyx','Pioneer','Quest','Ridge',
              'Solar','Tundra','Ultra','Vista','Willow','Yukon','Zephyr','Aero','Blaze','Canyon'])[p.model_n]
       || ' ' ||
       (ARRAY['кожаные','замшевые','текстильные','утеплённые',
              'летние','демисезонные','лакированные','сетчатые'])[p.adj_n],
       (ARRAY['Стэп','Тропа','Ритм','Север','Шаг','Подиум','Ветер','Горизонт','Атлант','Кедр'])[p.brand_n],
       'A-' || lpad(p.n::text, 6, '0'),
       :'t_end'::timestamptz - interval '400 days',
       :'t_end'::timestamptz - interval '400 days'
FROM g_products p JOIN g_cat c ON c.n = p.cat_n;

-- ---------------------------------------------------------------------
-- Пары: 5 размеров подряд × 2 цвета = 10 на модель
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_variants AS
SELECT row_number() OVER (ORDER BY p.n, s.k, c.k) AS n,
       uuid_v7() AS id, p.id AS product_id,
       (p.size0 + s.k)::text AS size,
       (ARRAY['чёрный','белый','коричневый','синий','серый','бежевый','красный','зелёный'])
         [((p.col1 - 1 + c.k) % 8) + 1] AS color,
       p.base_price
FROM g_products p
CROSS JOIN generate_series(0, 4) s(k)
CROSS JOIN generate_series(0, 1) c(k);

SELECT count(*) AS n_variants FROM g_variants \gset

INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode,
                              cost_price, sale_price, created_at, updated_at)
SELECT id, :'tenant', product_id, size, color,
       :'bc_prefix' || lpad(n::text, 11, '0'),
       round(base_price * 0.55, 2), base_price,
       :'t_end'::timestamptz - interval '400 days',
       :'t_end'::timestamptz - interval '400 days'
FROM g_variants;

-- ---------------------------------------------------------------------
-- Приход: первый филиал получает все пары, остальные — ~30%.
-- Накладные по 100 строк, сначала draft, затем accepted (триггер →
-- движения → остатки).
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_pitems AS
SELECT uuid_v7() AS id, s.id AS store_id, v.id AS variant_id, v.n AS vn,
       (1 + floor(6 * random()))::numeric(14,3) AS qty,
       round(v.base_price * 0.55, 2) AS cost_price
FROM g_stores s JOIN g_variants v ON (s.n = 1 OR random() < 0.3);

CREATE TEMP TABLE g_pitems2 AS
SELECT *, (row_number() OVER (PARTITION BY store_id ORDER BY vn) - 1) / 100 AS grp
FROM g_pitems;

CREATE TEMP TABLE g_purchases AS
SELECT store_id, grp, uuid_v7() AS id,
       sum(qty * cost_price)::numeric(14,2) AS total,
       :'t_end'::timestamptz - interval '375 days' + random() * interval '60 days' AS acc_at,
       1 + floor(random() * 30)::int AS supplier_n
FROM g_pitems2 GROUP BY store_id, grp;

INSERT INTO purchases (id, tenant_id, store_id, supplier_id, number, doc_date,
                       status, total, created_by, created_at, updated_at)
SELECT p.id, :'tenant', p.store_id, s.id, 'ПН-' || lpad(p.grp::text, 5, '0'),
       (p.acc_at AT TIME ZONE 'Asia/Bishkek')::date,
       'draft', p.total, o.id, p.acc_at, p.acc_at
FROM g_purchases p
JOIN g_suppliers s ON s.n = p.supplier_n
CROSS JOIN g_owner o;

INSERT INTO purchase_items (id, tenant_id, purchase_id, variant_id, qty, cost_price)
SELECT i.id, :'tenant', p.id, i.variant_id, i.qty, i.cost_price
FROM g_pitems2 i JOIN g_purchases p ON p.store_id = i.store_id AND p.grp = i.grp;

UPDATE purchases pu
   SET status = 'accepted', accepted_at = p.acc_at, accepted_by = o.id
  FROM g_purchases p CROSS JOIN g_owner o
 WHERE pu.id = p.id;

-- ---------------------------------------------------------------------
-- Смены: одна касса на филиал, одна смена в день, 09:00–21:00 по Бишкеку
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_shifts AS
SELECT row_number() OVER (ORDER BY s.n, d) AS i,
       uuid_v7() AS id, s.id AS store_id, s.n AS store_n, d AS day_n, u.id AS cashier_id,
       :'t_end'::timestamptz - make_interval(days => :days - d) + interval '3 hours'  AS opened_at,
       :'t_end'::timestamptz - make_interval(days => :days - d) + interval '15 hours' AS closed_at
FROM g_stores s
CROSS JOIN generate_series(0, :days - 1) d
JOIN g_users u ON u.store_id = s.id AND u.k = 1 + d % 2;

INSERT INTO cash_shifts (id, tenant_id, store_id, device_id, cashier_id, status,
                         opened_at, closed_at, opening_cash, synced_at)
SELECT id, :'tenant', store_id, 'kassa-' || store_n, cashier_id, 'closed',
       opened_at, closed_at, 5000, closed_at
FROM g_shifts;

-- ---------------------------------------------------------------------
-- Чеки. Строк в чеке 1–4 (в среднем ~1.5). Спрос неравномерный
-- (random()^3): самая ходовая пара продаётся ~2 000 раз за год —
-- худший случай для «истории движений пары».
-- ---------------------------------------------------------------------
CREATE TEMP TABLE g_sales0 AS
SELECT n, uuid_v7() AS id,
       1 + floor(random() * (SELECT count(*) FROM g_shifts))::int AS shift_i,
       random() AS t_frac,
       1 + floor(random() * random() * 3.999)::int AS n_items,
       (random() < 0.3) AS has_customer,
       random() AS r_cust,
       random() AS r_pay
FROM generate_series(1, :n_sales) n;

CREATE TEMP TABLE g_sales AS
SELECT s0.*, sh.id AS shift_id, sh.store_id, sh.store_n, sh.cashier_id, sh.closed_at,
       sh.opened_at + s0.t_frac * interval '12 hours' AS created_at
FROM g_sales0 s0 JOIN g_shifts sh ON sh.i = s0.shift_i;

CREATE TEMP TABLE g_items AS
SELECT uuid_v7() AS id, s.id AS sale_id, k,
       1 + floor(:n_variants * random() ^ 3)::int AS vn,
       (CASE WHEN random() < 0.9 THEN 1 ELSE 2 END)::numeric(14,3) AS qty,
       (random() < 0.15) AS discounted
FROM g_sales s CROSS JOIN LATERAL generate_series(1, s.n_items) k;

CREATE TEMP TABLE g_items2 AS
SELECT i.id, i.sale_id, i.k, i.qty, v.id AS variant_id,
       v.base_price AS price,
       round(v.base_price * 0.55, 2) AS cost_price,
       CASE WHEN i.discounted THEN round(v.base_price * i.qty * 0.1, 2) ELSE 0 END::numeric(14,2) AS discount
FROM g_items i JOIN g_variants v ON v.n = i.vn;

-- Возвраты: ~3% чеков, первая строка, 1 пара, в той же смене
CREATE TEMP TABLE g_returns AS
SELECT uuid_v7() AS id, s.id AS orig_id, s.shift_id, s.store_id, s.store_n, s.cashier_id,
       s.created_at + (s.closed_at - s.created_at) / 2 AS created_at
FROM g_sales s
WHERE random() < 0.03;

CREATE TEMP TABLE g_ritems AS
SELECT uuid_v7() AS id, r.id AS sale_id, i.id AS orig_item_id, i.variant_id,
       1::numeric(14,3) AS qty, i.price, i.cost_price,
       round(i.discount / i.qty, 2) AS discount
FROM g_returns r JOIN g_items2 i ON i.sale_id = r.orig_id AND i.k = 1;

CREATE TEMP TABLE g_docs AS
SELECT id, store_id, store_n, shift_id, cashier_id, created_at,
       'sale'::text AS type, NULL::uuid AS orig_id, has_customer, r_cust, r_pay
FROM g_sales
UNION ALL
SELECT id, store_id, store_n, shift_id, cashier_id, created_at,
       'return', orig_id, false, 0, random()
FROM g_returns;

CREATE TEMP TABLE g_doc_totals AS
SELECT sale_id, sum(price * qty)::numeric(14,2) AS subtotal, sum(discount)::numeric(14,2) AS discount_total
FROM (SELECT sale_id, price, qty, discount FROM g_items2
      UNION ALL
      SELECT sale_id, price, qty, discount FROM g_ritems) x
GROUP BY sale_id;

INSERT INTO sales (id, tenant_id, store_id, shift_id, cashier_id, customer_id, device_id,
                   local_number, type, return_of_sale_id, subtotal, discount_total, total,
                   created_at_local, synced_at)
SELECT d.id, :'tenant', d.store_id, d.shift_id, d.cashier_id, c.id,
       'kassa-' || d.store_n,
       row_number() OVER (PARTITION BY d.store_id ORDER BY d.created_at, d.id),
       d.type, d.orig_id, t.subtotal, t.discount_total, t.subtotal - t.discount_total,
       d.created_at, d.created_at + interval '5 seconds'
FROM g_docs d
JOIN g_doc_totals t ON t.sale_id = d.id
LEFT JOIN g_customers c ON d.has_customer AND c.n = 1 + floor(d.r_cust * :n_customers)::int
ORDER BY d.created_at;

-- Строки чеков → триггер sale_item_movement → stock_movements → stock
INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, qty, price, cost_price, discount, total)
SELECT id, :'tenant', sale_id, variant_id, qty, price, cost_price, discount, price * qty - discount
FROM g_items2 ORDER BY sale_id, k;

INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, return_of_item_id,
                        qty, price, cost_price, discount, total)
SELECT id, :'tenant', sale_id, variant_id, orig_item_id, qty, price, cost_price, discount,
       price * qty - discount
FROM g_ritems;

-- Оплаты: 50% наличные, 35% карта, 15% пополам. Возврат — наличными.
INSERT INTO payments (id, tenant_id, sale_id, method, amount, created_at)
SELECT uuid_v7(), :'tenant', d.id, p.method, p.amount, d.created_at
FROM g_docs d
JOIN g_doc_totals t ON t.sale_id = d.id
CROSS JOIN LATERAL (
  SELECT * FROM (VALUES
    ('cash', CASE WHEN d.type = 'return' OR d.r_pay < 0.5 THEN t.subtotal - t.discount_total
                  WHEN d.r_pay >= 0.85 THEN round((t.subtotal - t.discount_total) / 2, 2) END),
    ('card', CASE WHEN d.type = 'return' OR d.r_pay < 0.5 THEN NULL
                  WHEN d.r_pay < 0.85 THEN t.subtotal - t.discount_total
                  ELSE (t.subtotal - t.discount_total) - round((t.subtotal - t.discount_total) / 2, 2) END)
  ) v(method, amount)
  WHERE v.amount IS NOT NULL
) p;

-- ---------------------------------------------------------------------
-- Очередь синхронизации: история отправленного + ждущие записи
-- (часть уже пора отправлять, часть — ретраи в будущем)
-- ---------------------------------------------------------------------
INSERT INTO sync_outbox (id, tenant_id, device_id, entity, entity_id, op, payload,
                         attempts, next_attempt_at, created_at, sent_at)
SELECT uuid_v7(), :'tenant', 'kassa-1', (ARRAY['sale','product','stock','customer'])[1 + n % 4],
       gen_random_uuid(), 'upsert', jsonb_build_object('n', n, 'state', 'sent'),
       1, c, c, c + interval '2 seconds'
FROM (SELECT n, :'t_end'::timestamptz - random() * interval '365 days' AS c
        FROM generate_series(1, :outbox_sent) n) x;

INSERT INTO sync_outbox (id, tenant_id, device_id, entity, entity_id, op, payload,
                         attempts, next_attempt_at, created_at, sent_at)
SELECT uuid_v7(), :'tenant', 'kassa-' || (1 + n % :n_stores),
       (ARRAY['sale','product','stock','customer'])[1 + n % 4],
       gen_random_uuid(), 'upsert',
       jsonb_build_object('n', n, 'name', 'Кроссовки Runner кожаные', 'price', 4990, 'size', '42'),
       n % 5, now() + (random() - 0.6) * interval '10 minutes', now() - interval '1 minute', NULL
FROM generate_series(1, :outbox_pending) n;

COMMIT;
DISCARD TEMP;
