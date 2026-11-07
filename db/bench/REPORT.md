# Kassir ERP — замеры БД

Дата прогона: 2026-11-07 15:08 (Бишкек)  
Сервер: PostgreSQL 16.4 (Debian 16.4-1.pgdg120+2) on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14) 12.2.0, 64-bit  
shared_buffers = 128MB, work_mem = 4MB, effective_cache_size = 4GB, jit = on, random_page_cost = 4

Как мерили: каждый запрос выполнен ролью `kassir_app` с `app.tenant_id` основного тенанта — RLS включён, как в приложении. 1 первый прогон + 9 прогонов `EXPLAIN (ANALYZE, TIMING OFF)`; **итог = медиана (планирование + выполнение)**, сравнивается с целью из ТЗ. Сеть и передача результата клиенту в замер не входят. План ниже — отдельный прогон `EXPLAIN (ANALYZE, BUFFERS)`.

## Объёмы данных

| Таблица | Основной тенант | Всего в таблице | Размер с индексами |
|---|--:|--:|--:|
| products | 50000 | 55001 | 14 MB |
| product_variants | 500000 | 560000 | 167 MB |
| sales | 102974 | 113277 | 42 MB |
| sale_items | 162016 | 178172 | 34 MB |
| payments | 118024 | 129772 | 22 MB |
| cash_shifts | 1095 | 1460 | 352 kB |
| purchase_items | 1000000 | 1050000 | 161 MB |
| stock_movements | 1162016 | 1288172 | 464 MB |
| stock | 1042573 | 1102573 | 180 MB |
| sync_outbox | 201000 | 221100 | 47 MB |

Чеков основного тенанта: 100000 продаж + 2974 возвратов. Строк outbox, ждущих отправки: 1000. Целостность `stock = SUM(stock_movements)`: **совпадает**.

## Итог по запросам

| # | Запрос | Индекс | Индекс в плане | Цель ТЗ | Медиана | Первый прогон | Строк | Итог |
|--:|---|---|:-:|--:|--:|--:|--:|---|
| 1 | Скан штрихкода — пара есть | `ux_variants_barcode` | да | < 5 мс | 0.184 мс | 0.483 мс | 1 | ✅ пройдено |
| 2 | Скан штрихкода — нет в базе | `ux_variants_barcode` | да | < 5 мс | 0.167 мс | 0.218 мс | 0 | ✅ пройдено |
| 3 | Поиск по названию — редкий запрос «кроссовки runner» (50 товаров со всеми парами) | — | — | < 50 мс | 1.478 мс | 2.787 мс | 500 | ✅ пройдено |
| 4 | Поиск по названию — частый запрос «кроссовки» (50 товаров со всеми парами) | — | — | < 50 мс | 1.418 мс | 2.312 мс | 500 | ✅ пройдено |
| 5 | Поиск по названию — короткий запрос «run» (50 товаров со всеми парами) | — | — | < 50 мс | 1.205 мс | 1.770 мс | 500 | ✅ пройдено |
| 6 | Товары категории — самая большая, первая страница | `ix_products_category` | да | < 30 мс | 2.598 мс | 4.473 мс | 50 | ✅ пройдено |
| 7 | Отчёт за год — выручка по дням, один филиал | `ix_sales_store_date` | да | < 300 мс | 17.532 мс | 24.986 мс | 365 | ✅ пройдено |
| 8 | Отчёт за год — выручка, себестоимость, прибыль по дням, один филиал | `ix_sales_store_date` | да | < 300 мс | 122.844 мс | 119.824 мс | 365 | ✅ пройдено |
| 9 | Отчёт за год — выручка, себестоимость, прибыль по дням, все филиалы | — | — | < 1000 мс | 251.899 мс | 249.355 мс | 365 | ✅ пройдено |
| 10 | Закрытие смены — самая загруженная смена | `ix_sales_shift` | да | < 200 мс | 0.448 мс | 1.706 мс | 2 | ✅ пройдено |
| 11 | История движений — самая ходовая пара, вся история с остатком | `ix_movements_variant` | да | < 100 мс | 2.054 мс | 4.692 мс | 2019 | ✅ пройдено |
| 12 | История движений — обычная пара | `ix_movements_variant` | да | < 100 мс | 0.035 мс | 0.157 мс | 2 | ✅ пройдено |
| 13 | Очередь синхронизации — взять пачку 100 | `ix_outbox_pending` | да | < 10 мс | 0.091 мс | 0.452 мс | 100 | ✅ пройдено |
| 14 | Чек целиком — строки и оплаты одним запросом | `ix_sale_items_sale` | да | — | 0.092 мс | 0.226 мс | 1 | цели в ТЗ нет |
| 15 | Накладная целиком — 100 строк одним запросом | `ix_purchase_items_purchase` | да | — | 0.230 мс | 0.387 мс | 1 | цели в ТЗ нет |

Пройдено 13 из 13 запросов с целью в ТЗ.

## Запросы подробно

### 1. Скан штрихкода — пара есть

Цель ТЗ: < 5 мс · медиана **0.184 мс** (9 прогонов) · первый прогон 0.483 мс · мин 0.151 / макс 0.245 мс · индекс `ux_variants_barcode`

**Итог: ПРОЙДЕНО**

```sql

SELECT v.id, p.name, p.brand, v.size, v.color, v.sale_price, coalesce(s.qty, 0) AS qty
FROM product_variants v
JOIN products p ON p.id = v.product_id
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb' AND s.variant_id = v.id
WHERE v.barcode = '4600000250001' AND v.deleted_at IS NULL
```

```
Nested Loop Left Join  (cost=1.16..25.25 rows=1 width=119) (actual time=0.014..0.015 rows=1 loops=1)
  Buffers: shared hit=10
  ->  Nested Loop  (cost=0.72..16.78 rows=1 width=103) (actual time=0.011..0.011 rows=1 loops=1)
        Buffers: shared hit=7
        ->  Index Scan using ux_variants_barcode on product_variants v  (cost=0.43..8.46 rows=1 width=70) (actual time=0.007..0.008 rows=1 loops=1)
              Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (barcode = '4600000250001'::text))
              Buffers: shared hit=4
        ->  Index Scan using products_pkey on products p  (cost=0.29..8.32 rows=1 width=65) (actual time=0.002..0.002 rows=1 loops=1)
              Index Cond: (id = v.product_id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=3
  ->  Index Scan using stock_pkey on stock s  (cost=0.44..8.46 rows=1 width=36) (actual time=0.002..0.002 rows=0 loops=1)
        Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'::uuid) AND (variant_id = v.id))
        Buffers: shared hit=3
Planning:
  Buffers: shared hit=16
Planning Time: 0.164 ms
Execution Time: 0.032 ms
```

### 2. Скан штрихкода — нет в базе

Цель ТЗ: < 5 мс · медиана **0.167 мс** (9 прогонов) · первый прогон 0.218 мс · мин 0.136 / макс 0.220 мс · индекс `ux_variants_barcode`

**Итог: ПРОЙДЕНО**

```sql

SELECT v.id, p.name, p.brand, v.size, v.color, v.sale_price, coalesce(s.qty, 0) AS qty
FROM product_variants v
JOIN products p ON p.id = v.product_id
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb' AND s.variant_id = v.id
WHERE v.barcode = '0000000000000' AND v.deleted_at IS NULL
```

```
Nested Loop Left Join  (cost=1.16..25.25 rows=1 width=119) (actual time=0.004..0.005 rows=0 loops=1)
  Buffers: shared hit=3
  ->  Nested Loop  (cost=0.72..16.78 rows=1 width=103) (actual time=0.004..0.004 rows=0 loops=1)
        Buffers: shared hit=3
        ->  Index Scan using ux_variants_barcode on product_variants v  (cost=0.43..8.46 rows=1 width=70) (actual time=0.004..0.004 rows=0 loops=1)
              Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (barcode = '0000000000000'::text))
              Buffers: shared hit=3
        ->  Index Scan using products_pkey on products p  (cost=0.29..8.32 rows=1 width=65) (never executed)
              Index Cond: (id = v.product_id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
  ->  Index Scan using stock_pkey on stock s  (cost=0.44..8.46 rows=1 width=36) (never executed)
        Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'::uuid) AND (variant_id = v.id))
Planning:
  Buffers: shared hit=16
Planning Time: 0.151 ms
Execution Time: 0.016 ms
```

### 3. Поиск по названию — редкий запрос «кроссовки runner» (50 товаров со всеми парами)

Цель ТЗ: < 50 мс · медиана **1.478 мс** (9 прогонов) · первый прогон 2.787 мс · мин 1.437 / макс 2.020 мс · индекс — (см. комментарий в bench.sql)

**Итог: ПРОЙДЕНО**

```sql

SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM catalog.search_products('кроссовки runner', 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
```

```
Sort  (cost=18937.43..18960.01 rows=9034 width=118) (actual time=1.760..1.775 rows=500 loops=1)
  Sort Key: p.name, p.id, v.size, v.color
  Sort Method: quicksort  Memory: 92kB
  Buffers: shared hit=326
  ->  Nested Loop  (cost=0.68..18343.84 rows=9034 width=118) (actual time=0.963..1.331 rows=500 loops=1)
        Buffers: shared hit=326
        ->  Function Scan on search_products p  (cost=0.25..10.25 rows=1000 width=80) (actual time=0.952..0.955 rows=50 loops=1)
              Buffers: shared hit=109
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..18.24 rows=9 width=54) (actual time=0.003..0.006 rows=10 loops=50)
              Index Cond: (product_id = p.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=217
Planning Time: 0.123 ms
Execution Time: 1.801 ms
```

### 4. Поиск по названию — частый запрос «кроссовки» (50 товаров со всеми парами)

Цель ТЗ: < 50 мс · медиана **1.418 мс** (9 прогонов) · первый прогон 2.312 мс · мин 1.325 / макс 2.197 мс · индекс — (см. комментарий в bench.sql)

**Итог: ПРОЙДЕНО**

```sql

SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM catalog.search_products('кроссовки', 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
```

```
Sort  (cost=18937.43..18960.01 rows=9034 width=118) (actual time=1.324..1.338 rows=500 loops=1)
  Sort Key: p.name, p.id, v.size, v.color
  Sort Method: quicksort  Memory: 91kB
  Buffers: shared hit=220
  ->  Nested Loop  (cost=0.68..18343.84 rows=9034 width=118) (actual time=0.516..0.778 rows=500 loops=1)
        Buffers: shared hit=220
        ->  Function Scan on search_products p  (cost=0.25..10.25 rows=1000 width=80) (actual time=0.511..0.513 rows=50 loops=1)
              Buffers: shared hit=15
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..18.24 rows=9 width=54) (actual time=0.001..0.004 rows=10 loops=50)
              Index Cond: (product_id = p.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=205
Planning Time: 0.062 ms
Execution Time: 1.360 ms
```

### 5. Поиск по названию — короткий запрос «run» (50 товаров со всеми парами)

Цель ТЗ: < 50 мс · медиана **1.205 мс** (9 прогонов) · первый прогон 1.770 мс · мин 1.167 / макс 5.566 мс · индекс — (см. комментарий в bench.sql)

**Итог: ПРОЙДЕНО**

```sql

SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM catalog.search_products('run', 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
```

```
Sort  (cost=18937.43..18960.01 rows=9034 width=118) (actual time=1.131..1.145 rows=500 loops=1)
  Sort Key: p.name, p.id, v.size, v.color
  Sort Method: quicksort  Memory: 89kB
  Buffers: shared hit=272
  ->  Nested Loop  (cost=0.68..18343.84 rows=9034 width=118) (actual time=0.448..0.711 rows=500 loops=1)
        Buffers: shared hit=272
        ->  Function Scan on search_products p  (cost=0.25..10.25 rows=1000 width=80) (actual time=0.443..0.445 rows=50 loops=1)
              Buffers: shared hit=57
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..18.24 rows=9 width=54) (actual time=0.001..0.004 rows=10 loops=50)
              Index Cond: (product_id = p.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=215
Planning Time: 0.064 ms
Execution Time: 1.167 ms
```

### 6. Товары категории — самая большая, первая страница

Цель ТЗ: < 30 мс · медиана **2.598 мс** (9 прогонов) · первый прогон 4.473 мс · мин 2.484 / макс 2.787 мс · индекс `ix_products_category`

**Итог: ПРОЙДЕНО**

```sql

SELECT p.id, p.name, p.brand, p.article, pr.min_price, pr.max_price, pr.variants
FROM (SELECT id, name, brand, article FROM products
      WHERE category_id = '01a1b538-785e-7697-ba9f-82c032b6aaa4' AND deleted_at IS NULL
      ORDER BY name, id LIMIT 50) p
CROSS JOIN LATERAL (
  SELECT min(sale_price) AS min_price, max(sale_price) AS max_price, count(*) AS variants
  FROM product_variants v WHERE v.product_id = p.id AND v.deleted_at IS NULL) pr
ORDER BY p.name, p.id
```

```
Nested Loop  (cost=1670.07..2873.90 rows=50 width=146) (actual time=2.429..2.744 rows=50 loops=1)
  Buffers: shared hit=382
  ->  Limit  (cost=1645.53..1645.65 rows=50 width=74) (actual time=2.415..2.420 rows=50 loops=1)
        Buffers: shared hit=170
        ->  Sort  (cost=1645.53..1663.50 rows=7189 width=74) (actual time=2.415..2.417 rows=50 loops=1)
              Sort Key: products.name, products.id
              Sort Method: top-N heapsort  Memory: 36kB
              Buffers: shared hit=170
              ->  Bitmap Heap Scan on products  (cost=109.99..1406.71 rows=7189 width=74) (actual time=0.085..0.786 rows=7979 loops=1)
                    Recheck Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (category_id = '01a1b538-785e-7697-ba9f-82c032b6aaa4'::uuid))
                    Filter: (deleted_at IS NULL)
                    Heap Blocks: exact=162
                    Buffers: shared hit=170
                    ->  Bitmap Index Scan on ix_products_category  (cost=0.00..108.19 rows=7189 width=0) (actual time=0.072..0.073 rows=7979 loops=1)
                          Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (category_id = '01a1b538-785e-7697-ba9f-82c032b6aaa4'::uuid))
                          Buffers: shared hit=8
  ->  Aggregate  (cost=24.54..24.55 rows=1 width=72) (actual time=0.006..0.006 rows=1 loops=50)
        Buffers: shared hit=212
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..24.48 rows=9 width=5) (actual time=0.002..0.005 rows=10 loops=50)
              Index Cond: (product_id = products.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=212
Planning:
  Buffers: shared hit=1
Planning Time: 0.089 ms
Execution Time: 2.762 ms
```

### 7. Отчёт за год — выручка по дням, один филиал

Цель ТЗ: < 300 мс · медиана **17.532 мс** (9 прогонов) · первый прогон 24.986 мс · мин 17.000 / макс 18.287 мс · индекс `ix_sales_store_date`

**Итог: ПРОЙДЕНО**

```sql

SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       count(*) FILTER (WHERE s.type = 'sale')      AS checks,
       sum(s.total) FILTER (WHERE s.type = 'sale')   AS revenue,
       sum(s.total) FILTER (WHERE s.type = 'return') AS returns
FROM sales s
WHERE s.store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'
  AND s.created_at_local >= '2025-11-01 00:00:00+00' AND s.created_at_local < '2026-11-01 00:00:00+00'
GROUP BY 1 ORDER BY 1
```

```
GroupAggregate  (cost=7612.89..8853.53 rows=31016 width=76) (actual time=15.031..19.908 rows=365 loops=1)
  Group Key: (((created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
  Buffers: shared hit=2438
  ->  Sort  (cost=7612.89..7690.43 rows=31016 width=14) (actual time=15.006..15.975 rows=34492 loops=1)
        Sort Key: (((created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
        Sort Method: quicksort  Memory: 2884kB
        Buffers: shared hit=2438
        ->  Bitmap Heap Scan on sales s  (cost=1805.42..5298.98 rows=31016 width=14) (actual time=1.328..12.015 rows=34492 loops=1)
              Recheck Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
              Heap Blocks: exact=2189
              Buffers: shared hit=2438
              ->  Bitmap Index Scan on ix_sales_store_date  (cost=0.00..1797.67 rows=31016 width=0) (actual time=1.130..1.130 rows=34492 loops=1)
                    Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
                    Buffers: shared hit=249
Planning Time: 0.091 ms
Execution Time: 19.942 ms
```

### 8. Отчёт за год — выручка, себестоимость, прибыль по дням, один филиал

Цель ТЗ: < 300 мс · медиана **122.844 мс** (9 прогонов) · первый прогон 119.824 мс · мин 111.824 / макс 169.299 мс · индекс `ix_sales_store_date`

**Итог: ПРОЙДЕНО**

```sql

SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       sum(CASE WHEN s.type = 'sale' THEN i.total ELSE -i.total END)                       AS revenue,
       sum(CASE WHEN s.type = 'sale' THEN i.qty * i.cost_price ELSE -i.qty * i.cost_price END) AS cost,
       sum(CASE WHEN s.type = 'sale' THEN 1 ELSE -1 END * (i.total - i.qty * i.cost_price)) AS profit
FROM sales s
JOIN sale_items i ON i.sale_id = s.id
WHERE s.store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'
  AND s.created_at_local >= '2025-11-01 00:00:00+00' AND s.created_at_local < '2026-11-01 00:00:00+00'
GROUP BY 1 ORDER BY 1
```

```
GroupAggregate  (cost=16448.75..18922.33 rows=31016 width=100) (actual time=98.010..124.945 rows=365 loops=1)
  Group Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
  Buffers: shared hit=5272
  ->  Sort  (cost=16448.75..16559.73 rows=44393 width=25) (actual time=97.912..101.603 rows=54329 loops=1)
        Sort Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
        Sort Method: quicksort  Memory: 4070kB
        Buffers: shared hit=5272
        ->  Hash Join  (cost=5531.60..13022.04 rows=44393 width=25) (actual time=9.719..90.902 rows=54329 loops=1)
              Hash Cond: (i.sale_id = s.id)
              Buffers: shared hit=5272
              ->  Seq Scan on sale_items i  (cost=0.00..6842.87 rows=162131 width=32) (actual time=0.004..52.118 rows=162016 loops=1)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Rows Removed by Filter: 16156
                    Buffers: shared hit=2834
              ->  Hash  (cost=5143.90..5143.90 rows=31016 width=29) (actual time=9.683..9.684 rows=34492 loops=1)
                    Buckets: 65536 (originally 32768)  Batches: 1 (originally 1)  Memory Usage: 2569kB
                    Buffers: shared hit=2438
                    ->  Bitmap Heap Scan on sales s  (cost=1805.42..5143.90 rows=31016 width=29) (actual time=1.385..6.616 rows=34492 loops=1)
                          Recheck Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
                          Heap Blocks: exact=2189
                          Buffers: shared hit=2438
                          ->  Bitmap Index Scan on ix_sales_store_date  (cost=0.00..1797.67 rows=31016 width=0) (actual time=1.181..1.181 rows=34492 loops=1)
                                Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1b538-77cf-7dd2-b135-86ecd8aa6ddb'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
                                Buffers: shared hit=249
Planning:
  Buffers: shared hit=16
Planning Time: 0.185 ms
Execution Time: 125.006 ms
```

### 9. Отчёт за год — выручка, себестоимость, прибыль по дням, все филиалы

Цель ТЗ: < 1000 мс · медиана **251.899 мс** (9 прогонов) · первый прогон 249.355 мс · мин 246.258 / макс 257.895 мс · индекс — (см. комментарий в bench.sql)

**Итог: ПРОЙДЕНО**

```sql

SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       sum(CASE WHEN s.type = 'sale' THEN i.total ELSE -i.total END)                       AS revenue,
       sum(CASE WHEN s.type = 'sale' THEN i.qty * i.cost_price ELSE -i.qty * i.cost_price END) AS cost,
       sum(CASE WHEN s.type = 'sale' THEN 1 ELSE -1 END * (i.total - i.qty * i.cost_price)) AS profit
FROM sales s
JOIN sale_items i ON i.sale_id = s.id
WHERE s.created_at_local >= '2025-11-01 00:00:00+00' AND s.created_at_local < '2026-11-01 00:00:00+00'
GROUP BY 1 ORDER BY 1
```

```
GroupAggregate  (cost=30982.38..39189.34 rows=102907 width=100) (actual time=195.881..269.764 rows=365 loops=1)
  Group Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
  Buffers: shared hit=5242, temp read=699 written=702
  ->  Sort  (cost=30982.38..31350.60 rows=147289 width=25) (actual time=195.677..205.694 rows=162016 loops=1)
        Sort Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
        Sort Method: external merge  Disk: 5592kB
        Buffers: shared hit=5242, temp read=699 written=702
        ->  Hash Join  (cost=6809.45..14814.38 rows=147289 width=25) (actual time=48.007..167.512 rows=162016 loops=1)
              Hash Cond: (i.sale_id = s.id)
              Buffers: shared hit=5242
              ->  Seq Scan on sale_items i  (cost=0.00..6842.87 rows=162131 width=32) (actual time=0.005..53.636 rows=162016 loops=1)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Rows Removed by Filter: 16156
                    Buffers: shared hit=2834
              ->  Hash  (cost=5523.12..5523.12 rows=102907 width=29) (actual time=47.941..47.941 rows=102974 loops=1)
                    Buckets: 131072  Batches: 1  Memory Usage: 7165kB
                    Buffers: shared hit=2408
                    ->  Seq Scan on sales s  (cost=0.00..5523.12 rows=102907 width=29) (actual time=0.005..39.081 rows=102974 loops=1)
                          Filter: ((created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                          Rows Removed by Filter: 10303
                          Buffers: shared hit=2408
Planning:
  Buffers: shared hit=16
Planning Time: 0.183 ms
Execution Time: 270.219 ms
```

### 10. Закрытие смены — самая загруженная смена

Цель ТЗ: < 200 мс · медиана **0.448 мс** (9 прогонов) · первый прогон 1.706 мс · мин 0.397 / макс 0.592 мс · индекс `ix_sales_shift`

**Итог: ПРОЙДЕНО**

```sql

SELECT p.method,
       count(DISTINCT s.id) AS checks,
       sum(CASE WHEN s.type = 'sale' THEN p.amount ELSE -p.amount END) AS amount
FROM sales s
JOIN payments p ON p.sale_id = s.id
WHERE s.shift_id = '01a1b539-80b9-795f-9d40-df3f2523a853'
GROUP BY p.method
```

```
GroupAggregate  (cost=1285.80..1287.60 rows=2 width=45) (actual time=0.323..0.337 rows=2 loops=1)
  Group Key: p.method
  Buffers: shared hit=538
  ->  Sort  (cost=1285.80..1286.10 rows=118 width=31) (actual time=0.311..0.315 rows=150 loops=1)
        Sort Key: p.method, s.id
        Sort Method: quicksort  Memory: 33kB
        Buffers: shared hit=538
        ->  Nested Loop  (cost=0.71..1281.74 rows=118 width=31) (actual time=0.005..0.258 rows=150 loops=1)
              Buffers: shared hit=538
              ->  Index Scan using ix_sales_shift on sales s  (cost=0.29..386.04 rows=113 width=21) (actual time=0.002..0.047 rows=132 loops=1)
                    Index Cond: (shift_id = '01a1b539-80b9-795f-9d40-df3f2523a853'::uuid)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Buffers: shared hit=10
              ->  Index Scan using ix_payments_sale on payments p  (cost=0.42..7.92 rows=1 width=26) (actual time=0.001..0.001 rows=1 loops=132)
                    Index Cond: (sale_id = s.id)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Buffers: shared hit=528
Planning:
  Buffers: shared hit=17
Planning Time: 0.094 ms
Execution Time: 0.349 ms
```

### 11. История движений — самая ходовая пара, вся история с остатком

Цель ТЗ: < 100 мс · медиана **2.054 мс** (9 прогонов) · первый прогон 4.692 мс · мин 1.968 / макс 2.418 мс · индекс `ix_movements_variant`

**Итог: ПРОЙДЕНО**

```sql

SELECT m.occurred_at, m.reason, m.qty, m.store_id, m.doc_id,
       sum(m.qty) OVER (PARTITION BY m.store_id ORDER BY m.occurred_at, m.id) AS balance
FROM stock_movements m
WHERE m.variant_id = '01a1b538-7ee0-7b53-a54e-b9c6809f854a'
ORDER BY m.occurred_at DESC, m.id DESC
```

```
Sort  (cost=3243.25..3247.52 rows=1708 width=101) (actual time=2.493..2.552 rows=2019 loops=1)
  Sort Key: occurred_at DESC, id DESC
  Sort Method: quicksort  Memory: 238kB
  Buffers: shared hit=2031
  ->  WindowAgg  (cost=3113.11..3151.54 rows=1708 width=101) (actual time=1.395..2.224 rows=2019 loops=1)
        Buffers: shared hit=2031
        ->  Sort  (cost=3113.11..3117.38 rows=1708 width=69) (actual time=1.388..1.447 rows=2019 loops=1)
              Sort Key: store_id, occurred_at, id
              Sort Method: quicksort  Memory: 238kB
              Buffers: shared hit=2031
              ->  Index Scan using ix_movements_variant on stock_movements m  (cost=0.44..3021.41 rows=1708 width=69) (actual time=0.010..0.773 rows=2019 loops=1)
                    Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (variant_id = '01a1b538-7ee0-7b53-a54e-b9c6809f854a'::uuid))
                    Buffers: shared hit=2031
Planning Time: 0.135 ms
Execution Time: 2.670 ms
```

### 12. История движений — обычная пара

Цель ТЗ: < 100 мс · медиана **0.035 мс** (9 прогонов) · первый прогон 0.157 мс · мин 0.035 / макс 0.065 мс · индекс `ix_movements_variant`

**Итог: ПРОЙДЕНО**

```sql

SELECT m.occurred_at, m.reason, m.qty, m.store_id, m.doc_id,
       sum(m.qty) OVER (PARTITION BY m.store_id ORDER BY m.occurred_at, m.id) AS balance
FROM stock_movements m
WHERE m.variant_id = '01a1b538-8100-7a38-9989-7c2ebdc26a1c'
ORDER BY m.occurred_at DESC, m.id DESC
```

```
Sort  (cost=12.11..12.12 rows=3 width=101) (actual time=0.011..0.012 rows=2 loops=1)
  Sort Key: occurred_at DESC, id DESC
  Sort Method: quicksort  Memory: 25kB
  Buffers: shared hit=5
  ->  WindowAgg  (cost=12.02..12.09 rows=3 width=101) (actual time=0.009..0.010 rows=2 loops=1)
        Buffers: shared hit=5
        ->  Sort  (cost=12.02..12.03 rows=3 width=69) (actual time=0.006..0.006 rows=2 loops=1)
              Sort Key: store_id, occurred_at, id
              Sort Method: quicksort  Memory: 25kB
              Buffers: shared hit=5
              ->  Index Scan using ix_movements_variant on stock_movements m  (cost=0.44..12.00 rows=3 width=69) (actual time=0.004..0.005 rows=2 loops=1)
                    Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (variant_id = '01a1b538-8100-7a38-9989-7c2ebdc26a1c'::uuid))
                    Buffers: shared hit=5
Planning Time: 0.053 ms
Execution Time: 0.022 ms
```

### 13. Очередь синхронизации — взять пачку 100

Цель ТЗ: < 10 мс · медиана **0.091 мс** (9 прогонов) · первый прогон 0.452 мс · мин 0.088 / макс 0.128 мс · индекс `ix_outbox_pending`

**Итог: ПРОЙДЕНО**

```sql

SELECT id, entity, entity_id, op, payload, attempts
FROM sync_outbox
WHERE sent_at IS NULL AND next_attempt_at <= now()
ORDER BY next_attempt_at
LIMIT 100
FOR UPDATE SKIP LOCKED
```

```
Limit  (cost=0.28..408.35 rows=100 width=107) (actual time=0.007..0.081 rows=100 loops=1)
  Buffers: shared hit=300
  ->  LockRows  (cost=0.28..3656.64 rows=896 width=107) (actual time=0.006..0.076 rows=100 loops=1)
        Buffers: shared hit=300
        ->  Index Scan using ix_outbox_pending on sync_outbox  (cost=0.28..3647.68 rows=896 width=107) (actual time=0.006..0.054 rows=100 loops=1)
              Index Cond: (next_attempt_at <= now())
              Filter: ((sent_at IS NULL) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
              Buffers: shared hit=100
Planning Time: 0.031 ms
Execution Time: 0.091 ms
```

### 14. Чек целиком — строки и оплаты одним запросом

Цель ТЗ: нет · медиана **0.092 мс** (9 прогонов) · первый прогон 0.226 мс · мин 0.086 / макс 0.136 мс · индекс `ix_sale_items_sale`

**Итог: цели в ТЗ нет, для справки**

```sql

SELECT s.*,
       (SELECT json_agg(i ORDER BY i.id) FROM sale_items i WHERE i.sale_id = s.id) AS items,
       (SELECT json_agg(p ORDER BY p.id) FROM payments p WHERE p.sale_id = s.id)   AS payments
FROM sales s
WHERE s.id = '01a1b539-80ce-70e1-b550-049465d8a353'
```

```
Index Scan using sales_pkey on sales s  (cost=0.42..25.44 rows=1 width=222) (actual time=0.021..0.022 rows=1 loops=1)
  Index Cond: (id = '01a1b539-80ce-70e1-b550-049465d8a353'::uuid)
  Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
  Buffers: shared hit=12
  SubPlan 1
    ->  Aggregate  (cost=8.50..8.51 rows=1 width=32) (actual time=0.013..0.014 rows=1 loops=1)
          Buffers: shared hit=4
          ->  Sort  (cost=8.49..8.50 rows=2 width=144) (actual time=0.005..0.006 rows=4 loops=1)
                Sort Key: i.id
                Sort Method: quicksort  Memory: 25kB
                Buffers: shared hit=4
                ->  Index Scan using ix_sale_items_sale on sale_items i  (cost=0.42..8.48 rows=2 width=144) (actual time=0.002..0.004 rows=4 loops=1)
                      Index Cond: (sale_id = s.id)
                      Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                      Buffers: shared hit=4
  SubPlan 2
    ->  Aggregate  (cost=8.47..8.48 rows=1 width=32) (actual time=0.004..0.005 rows=1 loops=1)
          Buffers: shared hit=4
          ->  Sort  (cost=8.46..8.46 rows=1 width=106) (actual time=0.002..0.003 rows=1 loops=1)
                Sort Key: p.id
                Sort Method: quicksort  Memory: 25kB
                Buffers: shared hit=4
                ->  Index Scan using ix_payments_sale on payments p  (cost=0.42..8.45 rows=1 width=106) (actual time=0.002..0.002 rows=1 loops=1)
                      Index Cond: (sale_id = s.id)
                      Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                      Buffers: shared hit=4
Planning Time: 0.060 ms
Execution Time: 0.034 ms
```

### 15. Накладная целиком — 100 строк одним запросом

Цель ТЗ: нет · медиана **0.230 мс** (9 прогонов) · первый прогон 0.387 мс · мин 0.216 / макс 0.313 мс · индекс `ix_purchase_items_purchase`

**Итог: цели в ТЗ нет, для справки**

```sql

SELECT pu.*,
       (SELECT json_agg(pi ORDER BY pi.id) FROM purchase_items pi WHERE pi.purchase_id = pu.id) AS items
FROM purchases pu
WHERE pu.id = '01a1b538-ad1c-78da-b43c-fba44e286251'
```

```
Index Scan using purchases_pkey on purchases pu  (cost=0.29..390.57 rows=1 width=183) (actual time=0.244..0.246 rows=1 loops=1)
  Index Cond: (id = '01a1b538-ad1c-78da-b43c-fba44e286251'::uuid)
  Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
  Buffers: shared hit=8
  SubPlan 1
    ->  Aggregate  (cost=382.25..382.26 rows=1 width=32) (actual time=0.240..0.240 rows=1 loops=1)
          Buffers: shared hit=5
          ->  Sort  (cost=381.77..382.01 rows=94 width=115) (actual time=0.122..0.126 rows=100 loops=1)
                Sort Key: pi.id
                Sort Method: quicksort  Memory: 38kB
                Buffers: shared hit=5
                ->  Bitmap Heap Scan on purchase_items pi  (cost=5.19..378.69 rows=94 width=115) (actual time=0.065..0.107 rows=100 loops=1)
                      Recheck Cond: (purchase_id = pu.id)
                      Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                      Heap Blocks: exact=2
                      Buffers: shared hit=5
                      ->  Bitmap Index Scan on ix_purchase_items_purchase  (cost=0.00..5.17 rows=99 width=0) (actual time=0.002..0.003 rows=100 loops=1)
                            Index Cond: (purchase_id = pu.id)
                            Buffers: shared hit=3
Planning Time: 0.045 ms
Execution Time: 0.259 ms
```

## Стоимость триггеров

Вставка 10 000 строк в `stock_movements`: в T1–T6 каждая — отдельный `INSERT` (как строка чека), в T7–T8 — пачками по 1 000 строк одним `INSERT ... SELECT` (как импорт или инвентаризация). Время снято по пачкам из 1 000 строк. Рост = (среднее за последние 3 пачки) / (среднее за первые 3). База для сравнения — копия журнала с теми же данными, индексами, FK и RLS, но без триггера `movement_to_stock`. В T5/T6 `synchronous_commit = off`, чтобы мерить триггер, а не fsync диска.

| Код | Сценарий | Строк | Всего, мс | мс/строку | Первые 3 пачки, мс/строку | Последние 3 пачки, мс/строку | Рост | Время растёт? |
|---|---|--:|--:|--:|--:|--:|--:|---|
| T1 | Без триггера (копия журнала): 10 000 разных пар, одна транзакция | 10000 | 306 | 0.0306 | 0.0294 | 0.0343 | ×1.17 | нет |
| T2 | С триггером: 10 000 разных пар, строк в stock ещё нет (INSERT), одна транзакция | 10000 | 550 | 0.0550 | 0.0547 | 0.0585 | ×1.07 | нет |
| T3 | С триггером: те же 10 000 пар, строки в stock есть (UPDATE), одна транзакция | 10000 | 517 | 0.0517 | 0.0500 | 0.0519 | ×1.04 | нет |
| T4 | С триггером: одна пара 10 000 раз, одна транзакция | 10000 | 2573 | 0.2573 | 0.1111 | 0.4203 | ×3.78 | **да** |
| T5 | Без триггера (копия журнала): одна пара 10 000 раз, COMMIT на каждую строку | 10000 | 602 | 0.0602 | 0.0598 | 0.0600 | ×1.00 | нет |
| T6 | С триггером: одна пара 10 000 раз, COMMIT на каждую строку | 10000 | 861 | 0.0861 | 0.0851 | 0.0906 | ×1.06 | нет |
| T7 | С триггером: одна пара 10 000 раз, пачками по 1 000 (INSERT ... SELECT), одна транзакция | 10000 | 161 | 0.0161 | 0.0164 | 0.0155 | ×0.95 | нет |
| T8 | С триггером: 10 000 разных пар, пачками по 1 000 (INSERT ... SELECT), одна транзакция | 10000 | 274 | 0.0274 | 0.0346 | 0.0259 | ×0.75 | нет |

Цена триггера `movement_to_stock` (разница с копией без триггера):

- новая строка остатка (INSERT в stock): **0.0244 мс/строку** (T2 − T1)
- существующая строка остатка (UPDATE stock): **0.0211 мс/строку** (T3 − T1)
- одна и та же пара, коммит на строку: **0.0259 мс/строку** (T6 − T5)
- одна и та же пара 10 000 раз в одной транзакции, по строке: **0.2267 мс/строку** (T4 − T1)
- одна и та же пара 10 000 раз в одной транзакции, пачками: **0.0161 мс/строку** всего (T7) — в 16.0 раз быстрее T4
- 10 000 разных пар пачками: **0.0274 мс/строку** всего (T8) против 0.0517 по строке (T3)

### По пачкам, мс на строку

| Код | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| T1 | 0.0311 | 0.0289 | 0.0282 | 0.0303 | 0.0280 | 0.0276 | 0.0289 | 0.0335 | 0.0405 | 0.0289 |
| T2 | 0.0560 | 0.0539 | 0.0542 | 0.0527 | 0.0528 | 0.0520 | 0.0525 | 0.0569 | 0.0571 | 0.0615 |
| T3 | 0.0485 | 0.0537 | 0.0477 | 0.0559 | 0.0568 | 0.0495 | 0.0496 | 0.0494 | 0.0552 | 0.0511 |
| T4 | 0.0948 | 0.1037 | 0.1349 | 0.1873 | 0.2359 | 0.2594 | 0.2964 | 0.3606 | 0.4097 | 0.4905 |
| T5 | 0.0592 | 0.0587 | 0.0615 | 0.0598 | 0.0605 | 0.0610 | 0.0608 | 0.0596 | 0.0595 | 0.0611 |
| T6 | 0.0902 | 0.0830 | 0.0821 | 0.0830 | 0.0841 | 0.0831 | 0.0839 | 0.0834 | 0.1037 | 0.0847 |
| T7 | 0.0149 | 0.0145 | 0.0197 | 0.0168 | 0.0177 | 0.0159 | 0.0145 | 0.0162 | 0.0144 | 0.0161 |
| T8 | 0.0443 | 0.0356 | 0.0241 | 0.0235 | 0.0239 | 0.0220 | 0.0229 | 0.0325 | 0.0222 | 0.0231 |

## Параметры замера

| Ключ | Значение |
|---|---|
| bc | `4600000250001` |
| bc_missing | `0000000000000` |
| cat | `01a1b538-785e-7697-ba9f-82c032b6aaa4` |
| period_from | `2025-11-01 00:00:00+00` |
| period_to | `2026-11-01 00:00:00+00` |
| purchase_doc | `01a1b538-ad1c-78da-b43c-fba44e286251` |
| q_freq | `кроссовки` |
| q_rare | `кроссовки runner` |
| q_short | `run` |
| sale_doc | `01a1b539-80ce-70e1-b550-049465d8a353` |
| shift | `01a1b539-80b9-795f-9d40-df3f2523a853` |
| store | `01a1b538-77cf-7dd2-b135-86ecd8aa6ddb` |
| trig_store | `01a1b543-a6f3-7fa7-88d9-1fa489eee8e7` |
| variant_hot | `01a1b538-7ee0-7b53-a54e-b9c6809f854a` |
| variant_typ | `01a1b538-8100-7a38-9989-7c2ebdc26a1c` |

