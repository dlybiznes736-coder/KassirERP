# Kassir ERP — замеры БД

Дата прогона: 2026-11-06 12:27 (Бишкек)  
Сервер: PostgreSQL 16.4 (Debian 16.4-1.pgdg120+2) on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14) 12.2.0, 64-bit  
shared_buffers = 128MB, work_mem = 4MB, effective_cache_size = 4GB, jit = on, random_page_cost = 4

Как мерили: каждый запрос выполнен ролью `kassir_app` с `app.tenant_id` основного тенанта — RLS включён, как в приложении. 1 первый прогон + 9 прогонов `EXPLAIN (ANALYZE, TIMING OFF)`; **итог = медиана (планирование + выполнение)**, сравнивается с целью из ТЗ. Сеть и передача результата клиенту в замер не входят. План ниже — отдельный прогон `EXPLAIN (ANALYZE, BUFFERS)`.

## Объёмы данных

| Таблица | Основной тенант | Всего в таблице | Размер с индексами |
|---|--:|--:|--:|
| products | 50000 | 55001 | 18 MB |
| product_variants | 500000 | 560000 | 167 MB |
| sales | 102974 | 113277 | 42 MB |
| sale_items | 162016 | 178172 | 34 MB |
| payments | 118024 | 129772 | 22 MB |
| cash_shifts | 1095 | 1460 | 360 kB |
| purchase_items | 1000000 | 1050000 | 160 MB |
| stock_movements | 1162016 | 1288172 | 470 MB |
| stock | 1042573 | 1102573 | 182 MB |
| sync_outbox | 201000 | 221100 | 47 MB |

Чеков основного тенанта: 100000 продаж + 2974 возвратов. Строк outbox, ждущих отправки: 1000. Целостность `stock = SUM(stock_movements)`: **совпадает**.

## Итог по запросам

| # | Запрос | Индекс | Индекс в плане | Цель ТЗ | Медиана | Первый прогон | Строк | Итог |
|--:|---|---|:-:|--:|--:|--:|--:|---|
| 1 | Скан штрихкода — пара есть | `ux_variants_barcode` | да | < 5 мс | 0.200 мс | 0.485 мс | 1 | ✅ пройдено |
| 2 | Скан штрихкода — нет в базе | `ux_variants_barcode` | да | < 5 мс | 0.147 мс | 0.230 мс | 0 | ✅ пройдено |
| 3 | Поиск по названию — редкий запрос «кроссовки runner» (50 товаров со всеми парами) | `ix_products_name_trgm` | **нет** | < 50 мс | 42.796 мс | 46.257 мс | 500 | ✅ пройдено |
| 4 | Поиск по названию — частый запрос «кроссовки» (50 товаров со всеми парами) | `ix_products_name_trgm` | **нет** | < 50 мс | 43.925 мс | 43.898 мс | 500 | ✅ пройдено |
| 5 | Поиск по названию — короткий запрос «run» (50 товаров со всеми парами) | `ix_products_name_trgm` | **нет** | < 50 мс | 36.906 мс | 38.949 мс | 500 | ✅ пройдено |
| 6 | Товары категории — самая большая, первая страница | `ix_products_category` | да | < 30 мс | 2.640 мс | 3.051 мс | 50 | ✅ пройдено |
| 7 | Отчёт за год — выручка по дням, один филиал | `ix_sales_store_date` | да | < 300 мс | 16.285 мс | 23.798 мс | 365 | ✅ пройдено |
| 8 | Отчёт за год — выручка, себестоимость, прибыль по дням, один филиал | `ix_sales_store_date` | да | < 300 мс | 104.884 мс | 127.336 мс | 365 | ✅ пройдено |
| 9 | Отчёт за год — выручка, себестоимость, прибыль по дням, все филиалы | — (Seq Scan) | — | < 1000 мс | 233.759 мс | 230.875 мс | 365 | ✅ пройдено |
| 10 | Закрытие смены — самая загруженная смена | `ix_sales_shift` | да | < 200 мс | 0.416 мс | 0.997 мс | 2 | ✅ пройдено |
| 11 | История движений — самая ходовая пара, вся история с остатком | `ix_movements_variant` | да | < 100 мс | 2.104 мс | 4.830 мс | 2019 | ✅ пройдено |
| 12 | История движений — обычная пара | `ix_movements_variant` | да | < 100 мс | 0.046 мс | 0.113 мс | 2 | ✅ пройдено |
| 13 | Очередь синхронизации — взять пачку 100 | `ix_outbox_pending` | да | < 10 мс | 0.091 мс | 0.369 мс | 100 | ✅ пройдено |
| 14 | Чек целиком — строки и оплаты одним запросом | `ix_sale_items_sale` | да | — | 0.103 мс | 0.210 мс | 1 | цели в ТЗ нет |
| 15 | Накладная целиком — 100 строк одним запросом | `ix_purchase_items_purchase` | да | — | 0.217 мс | 0.380 мс | 1 | цели в ТЗ нет |

Пройдено 13 из 13 запросов с целью в ТЗ.

## Запросы подробно

### 1. Скан штрихкода — пара есть

Цель ТЗ: < 5 мс · медиана **0.200 мс** (9 прогонов) · первый прогон 0.485 мс · мин 0.186 / макс 0.259 мс · индекс `ux_variants_barcode`

**Итог: ПРОЙДЕНО**

```sql
SELECT v.id, p.name, p.brand, v.size, v.color, v.sale_price, coalesce(s.qty, 0) AS qty
FROM product_variants v
JOIN products p ON p.id = v.product_id
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = '01a1af88-838f-7d03-8881-5057ae5481c8' AND s.variant_id = v.id
WHERE v.barcode = '4600000250001' AND v.deleted_at IS NULL
```

```
Nested Loop Left Join  (cost=1.16..25.25 rows=1 width=119) (actual time=0.021..0.022 rows=1 loops=1)
  Buffers: shared hit=10
  ->  Nested Loop  (cost=0.72..16.78 rows=1 width=103) (actual time=0.016..0.017 rows=1 loops=1)
        Buffers: shared hit=7
        ->  Index Scan using ux_variants_barcode on product_variants v  (cost=0.43..8.46 rows=1 width=70) (actual time=0.011..0.012 rows=1 loops=1)
              Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (barcode = '4600000250001'::text))
              Buffers: shared hit=4
        ->  Index Scan using products_pkey on products p  (cost=0.29..8.32 rows=1 width=65) (actual time=0.003..0.003 rows=1 loops=1)
              Index Cond: (id = v.product_id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=3
  ->  Index Scan using stock_pkey on stock s  (cost=0.44..8.46 rows=1 width=36) (actual time=0.003..0.003 rows=0 loops=1)
        Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'::uuid) AND (variant_id = v.id))
        Buffers: shared hit=3
Planning:
  Buffers: shared hit=16
Planning Time: 0.214 ms
Execution Time: 0.041 ms
```

### 2. Скан штрихкода — нет в базе

Цель ТЗ: < 5 мс · медиана **0.147 мс** (9 прогонов) · первый прогон 0.230 мс · мин 0.130 / макс 0.182 мс · индекс `ux_variants_barcode`

**Итог: ПРОЙДЕНО**

```sql
SELECT v.id, p.name, p.brand, v.size, v.color, v.sale_price, coalesce(s.qty, 0) AS qty
FROM product_variants v
JOIN products p ON p.id = v.product_id
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = '01a1af88-838f-7d03-8881-5057ae5481c8' AND s.variant_id = v.id
WHERE v.barcode = '0000000000000' AND v.deleted_at IS NULL
```

```
Nested Loop Left Join  (cost=1.16..25.25 rows=1 width=119) (actual time=0.005..0.005 rows=0 loops=1)
  Buffers: shared hit=3
  ->  Nested Loop  (cost=0.72..16.78 rows=1 width=103) (actual time=0.005..0.005 rows=0 loops=1)
        Buffers: shared hit=3
        ->  Index Scan using ux_variants_barcode on product_variants v  (cost=0.43..8.46 rows=1 width=70) (actual time=0.004..0.004 rows=0 loops=1)
              Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (barcode = '0000000000000'::text))
              Buffers: shared hit=3
        ->  Index Scan using products_pkey on products p  (cost=0.29..8.32 rows=1 width=65) (never executed)
              Index Cond: (id = v.product_id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
  ->  Index Scan using stock_pkey on stock s  (cost=0.44..8.46 rows=1 width=36) (never executed)
        Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'::uuid) AND (variant_id = v.id))
Planning:
  Buffers: shared hit=16
Planning Time: 0.151 ms
Execution Time: 0.018 ms
```

### 3. Поиск по названию — редкий запрос «кроссовки runner» (50 товаров со всеми парами)

Цель ТЗ: < 50 мс · медиана **42.796 мс** (9 прогонов) · первый прогон 46.257 мс · мин 42.476 / макс 44.337 мс · индекс `ix_products_name_trgm`

**Итог: ПРОЙДЕНО**

```sql
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM (SELECT id, name, brand FROM products
      WHERE deleted_at IS NULL AND name ILIKE '%' || 'кроссовки runner' || '%'
      ORDER BY name, id LIMIT 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
```

```
Incremental Sort  (cost=2495.15..2616.98 rows=46 width=103) (actual time=42.216..42.650 rows=500 loops=1)
  Sort Key: products.name, products.id, v.size, v.color
  Presorted Key: products.name, products.id
  Full-sort Groups: 13  Sort Method: quicksort  Average Memory: 30kB  Peak Memory: 30kB
  Buffers: shared hit=1331
  ->  Nested Loop  (cost=2492.48..2614.91 rows=46 width=103) (actual time=42.163..42.488 rows=500 loops=1)
        Buffers: shared hit=1331
        ->  Limit  (cost=2492.06..2492.07 rows=5 width=65) (actual time=42.152..42.158 rows=50 loops=1)
              Buffers: shared hit=1117
              ->  Sort  (cost=2492.06..2492.07 rows=5 width=65) (actual time=42.151..42.154 rows=50 loops=1)
                    Sort Key: products.name, products.id
                    Sort Method: quicksort  Memory: 36kB
                    Buffers: shared hit=1117
                    ->  Seq Scan on products  (cost=0.00..2492.00 rows=5 width=65) (actual time=0.168..42.069 rows=90 loops=1)
                          Filter: ((deleted_at IS NULL) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (name ~~* '%кроссовки runner%'::text))
                          Rows Removed by Filter: 54911
                          Buffers: shared hit=1117
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..24.48 rows=9 width=54) (actual time=0.002..0.006 rows=10 loops=50)
              Index Cond: (product_id = products.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=214
Planning Time: 0.119 ms
Execution Time: 42.676 ms
```

### 4. Поиск по названию — частый запрос «кроссовки» (50 товаров со всеми парами)

Цель ТЗ: < 50 мс · медиана **43.925 мс** (9 прогонов) · первый прогон 43.898 мс · мин 43.368 / макс 46.377 мс · индекс `ix_products_name_trgm`

**Итог: ПРОЙДЕНО**

```sql
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM (SELECT id, name, brand FROM products
      WHERE deleted_at IS NULL AND name ILIKE '%' || 'кроссовки' || '%'
      ORDER BY name, id LIMIT 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
```

```
Incremental Sort  (cost=2495.15..2616.98 rows=46 width=103) (actual time=43.137..43.556 rows=500 loops=1)
  Sort Key: products.name, products.id, v.size, v.color
  Presorted Key: products.name, products.id
  Full-sort Groups: 13  Sort Method: quicksort  Average Memory: 30kB  Peak Memory: 30kB
  Buffers: shared hit=1324
  ->  Nested Loop  (cost=2492.48..2614.91 rows=46 width=103) (actual time=43.093..43.402 rows=500 loops=1)
        Buffers: shared hit=1324
        ->  Limit  (cost=2492.06..2492.07 rows=5 width=65) (actual time=43.080..43.085 rows=50 loops=1)
              Buffers: shared hit=1117
              ->  Sort  (cost=2492.06..2492.07 rows=5 width=65) (actual time=43.079..43.082 rows=50 loops=1)
                    Sort Key: products.name, products.id
                    Sort Method: top-N heapsort  Memory: 36kB
                    Buffers: shared hit=1117
                    ->  Seq Scan on products  (cost=0.00..2492.00 rows=5 width=65) (actual time=0.014..41.061 rows=4886 loops=1)
                          Filter: ((deleted_at IS NULL) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (name ~~* '%кроссовки%'::text))
                          Rows Removed by Filter: 50115
                          Buffers: shared hit=1117
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..24.48 rows=9 width=54) (actual time=0.002..0.005 rows=10 loops=50)
              Index Cond: (product_id = products.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=207
Planning Time: 0.126 ms
Execution Time: 43.583 ms
```

### 5. Поиск по названию — короткий запрос «run» (50 товаров со всеми парами)

Цель ТЗ: < 50 мс · медиана **36.906 мс** (9 прогонов) · первый прогон 38.949 мс · мин 35.783 / макс 68.341 мс · индекс `ix_products_name_trgm`

**Итог: ПРОЙДЕНО**

```sql
SELECT p.id, p.name, p.brand, v.id AS variant_id, v.size, v.color, v.sale_price
FROM (SELECT id, name, brand FROM products
      WHERE deleted_at IS NULL AND name ILIKE '%' || 'run' || '%'
      ORDER BY name, id LIMIT 50) p
JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
ORDER BY p.name, p.id, v.size, v.color
```

```
Incremental Sort  (cost=2561.45..3788.50 rows=463 width=103) (actual time=38.765..39.216 rows=500 loops=1)
  Sort Key: products.name, products.id, v.size, v.color
  Presorted Key: products.name, products.id
  Full-sort Groups: 13  Sort Method: quicksort  Average Memory: 30kB  Peak Memory: 30kB
  Buffers: shared hit=1331
  ->  Nested Loop  (cost=2558.83..3767.67 rows=463 width=103) (actual time=38.717..39.067 rows=500 loops=1)
        Buffers: shared hit=1331
        ->  Limit  (cost=2558.41..2558.53 rows=50 width=65) (actual time=38.702..38.707 rows=50 loops=1)
              Buffers: shared hit=1117
              ->  Sort  (cost=2558.41..2563.40 rows=1999 width=65) (actual time=38.700..38.703 rows=50 loops=1)
                    Sort Key: products.name, products.id
                    Sort Method: top-N heapsort  Memory: 36kB
                    Buffers: shared hit=1117
                    ->  Seq Scan on products  (cost=0.00..2492.00 rows=1999 width=65) (actual time=0.144..38.247 rows=829 loops=1)
                          Filter: ((deleted_at IS NULL) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (name ~~* '%run%'::text))
                          Rows Removed by Filter: 54172
                          Buffers: shared hit=1117
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..24.09 rows=9 width=54) (actual time=0.003..0.006 rows=10 loops=50)
              Index Cond: (product_id = products.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=214
Planning Time: 0.142 ms
Execution Time: 39.246 ms
```

### 6. Товары категории — самая большая, первая страница

Цель ТЗ: < 30 мс · медиана **2.640 мс** (9 прогонов) · первый прогон 3.051 мс · мин 2.611 / макс 3.896 мс · индекс `ix_products_category`

**Итог: ПРОЙДЕНО**

```sql
SELECT p.id, p.name, p.brand, p.article, pr.min_price, pr.max_price, pr.variants
FROM (SELECT id, name, brand, article FROM products
      WHERE category_id = '01a1af88-840e-73f7-a893-b6956c880ab3' AND deleted_at IS NULL
      ORDER BY name, id LIMIT 50) p
CROSS JOIN LATERAL (
  SELECT min(sale_price) AS min_price, max(sale_price) AS max_price, count(*) AS variants
  FROM product_variants v WHERE v.product_id = p.id AND v.deleted_at IS NULL) pr
ORDER BY p.name, p.id
```

```
Nested Loop  (cost=1678.29..2882.13 rows=50 width=146) (actual time=2.544..2.869 rows=50 loops=1)
  Buffers: shared hit=385
  ->  Limit  (cost=1653.74..1653.87 rows=50 width=74) (actual time=2.530..2.535 rows=50 loops=1)
        Buffers: shared hit=170
        ->  Sort  (cost=1653.74..1672.01 rows=7309 width=74) (actual time=2.530..2.532 rows=50 loops=1)
              Sort Key: products.name, products.id
              Sort Method: top-N heapsort  Memory: 36kB
              Buffers: shared hit=170
              ->  Bitmap Heap Scan on products  (cost=111.22..1410.94 rows=7309 width=74) (actual time=0.102..0.805 rows=7979 loops=1)
                    Recheck Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (category_id = '01a1af88-840e-73f7-a893-b6956c880ab3'::uuid))
                    Filter: (deleted_at IS NULL)
                    Heap Blocks: exact=162
                    Buffers: shared hit=170
                    ->  Bitmap Index Scan on ix_products_category  (cost=0.00..109.39 rows=7309 width=0) (actual time=0.089..0.089 rows=7979 loops=1)
                          Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (category_id = '01a1af88-840e-73f7-a893-b6956c880ab3'::uuid))
                          Buffers: shared hit=8
  ->  Aggregate  (cost=24.55..24.56 rows=1 width=72) (actual time=0.006..0.006 rows=1 loops=50)
        Buffers: shared hit=215
        ->  Index Scan using ux_variants_product_size_color on product_variants v  (cost=0.42..24.48 rows=9 width=5) (actual time=0.002..0.005 rows=10 loops=50)
              Index Cond: (product_id = products.id)
              Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
              Buffers: shared hit=215
Planning Time: 0.094 ms
Execution Time: 2.888 ms
```

### 7. Отчёт за год — выручка по дням, один филиал

Цель ТЗ: < 300 мс · медиана **16.285 мс** (9 прогонов) · первый прогон 23.798 мс · мин 16.153 / макс 18.066 мс · индекс `ix_sales_store_date`

**Итог: ПРОЙДЕНО**

```sql
SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       count(*) FILTER (WHERE s.type = 'sale')      AS checks,
       sum(s.total) FILTER (WHERE s.type = 'sale')   AS revenue,
       sum(s.total) FILTER (WHERE s.type = 'return') AS returns
FROM sales s
WHERE s.store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'
  AND s.created_at_local >= '2025-11-01 00:00:00+00' AND s.created_at_local < '2026-11-01 00:00:00+00'
GROUP BY 1 ORDER BY 1
```

```
GroupAggregate  (cost=7696.57..8956.49 rows=31498 width=76) (actual time=14.083..18.810 rows=365 loops=1)
  Group Key: (((created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
  Buffers: shared hit=2634
  ->  Sort  (cost=7696.57..7775.32 rows=31498 width=14) (actual time=14.060..15.029 rows=34492 loops=1)
        Sort Key: (((created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
        Sort Method: quicksort  Memory: 2884kB
        Buffers: shared hit=2634
        ->  Bitmap Heap Scan on sales s  (cost=1832.77..5343.20 rows=31498 width=14) (actual time=1.333..11.302 rows=34492 loops=1)
              Recheck Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
              Heap Blocks: exact=2189
              Buffers: shared hit=2634
              ->  Bitmap Index Scan on ix_sales_store_date  (cost=0.00..1824.90 rows=31498 width=0) (actual time=1.139..1.139 rows=34492 loops=1)
                    Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
                    Buffers: shared hit=445
Planning Time: 0.093 ms
Execution Time: 18.841 ms
```

### 8. Отчёт за год — выручка, себестоимость, прибыль по дням, один филиал

Цель ТЗ: < 300 мс · медиана **104.884 мс** (9 прогонов) · первый прогон 127.336 мс · мин 103.026 / макс 122.067 мс · индекс `ix_sales_store_date`

**Итог: ПРОЙДЕНО**

```sql
SELECT (s.created_at_local AT TIME ZONE 'Asia/Bishkek')::date AS day,
       sum(CASE WHEN s.type = 'sale' THEN i.total ELSE -i.total END)                       AS revenue,
       sum(CASE WHEN s.type = 'sale' THEN i.qty * i.cost_price ELSE -i.qty * i.cost_price END) AS cost,
       sum(CASE WHEN s.type = 'sale' THEN 1 ELSE -1 END * (i.total - i.qty * i.cost_price)) AS profit
FROM sales s
JOIN sale_items i ON i.sale_id = s.id
WHERE s.store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'
  AND s.created_at_local >= '2025-11-01 00:00:00+00' AND s.created_at_local < '2026-11-01 00:00:00+00'
GROUP BY 1 ORDER BY 1
```

```
GroupAggregate  (cost=16549.31..19057.69 rows=31498 width=100) (actual time=95.995..119.477 rows=365 loops=1)
  Group Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
  Buffers: shared hit=5468
  ->  Sort  (cost=16549.31..16661.79 rows=44992 width=25) (actual time=95.901..98.591 rows=54329 loops=1)
        Sort Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
        Sort Method: quicksort  Memory: 4070kB
        Buffers: shared hit=5468
        ->  Hash Join  (cost=5579.44..13072.01 rows=44992 width=25) (actual time=9.261..89.176 rows=54329 loops=1)
              Hash Cond: (i.sale_id = s.id)
              Buffers: shared hit=5468
              ->  Seq Scan on sale_items i  (cost=0.00..6842.87 rows=161804 width=32) (actual time=0.004..51.580 rows=162016 loops=1)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Rows Removed by Filter: 16156
                    Buffers: shared hit=2834
              ->  Hash  (cost=5185.71..5185.71 rows=31498 width=29) (actual time=9.232..9.233 rows=34492 loops=1)
                    Buckets: 65536 (originally 32768)  Batches: 1 (originally 1)  Memory Usage: 2569kB
                    Buffers: shared hit=2634
                    ->  Bitmap Heap Scan on sales s  (cost=1832.77..5185.71 rows=31498 width=29) (actual time=1.436..6.357 rows=34492 loops=1)
                          Recheck Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
                          Heap Blocks: exact=2189
                          Buffers: shared hit=2634
                          ->  Bitmap Index Scan on ix_sales_store_date  (cost=0.00..1824.90 rows=31498 width=0) (actual time=1.236..1.236 rows=34492 loops=1)
                                Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (store_id = '01a1af88-838f-7d03-8881-5057ae5481c8'::uuid) AND (created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone))
                                Buffers: shared hit=445
Planning:
  Buffers: shared hit=17
Planning Time: 0.190 ms
Execution Time: 119.528 ms
```

### 9. Отчёт за год — выручка, себестоимость, прибыль по дням, все филиалы

Цель ТЗ: < 1000 мс · медиана **233.759 мс** (9 прогонов) · первый прогон 230.875 мс · мин 232.200 / макс 245.111 мс · индекс не нужен (Seq Scan)

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
GroupAggregate  (cost=30980.59..39190.40 rows=103092 width=100) (actual time=180.639..254.507 rows=365 loops=1)
  Group Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
  Buffers: shared hit=5242, temp read=699 written=702
  ->  Sort  (cost=30980.59..31348.73 rows=147256 width=25) (actual time=180.436..190.383 rows=162016 loops=1)
        Sort Key: (((s.created_at_local AT TIME ZONE 'Asia/Bishkek'::text))::date)
        Sort Method: external merge  Disk: 5592kB
        Buffers: shared hit=5242, temp read=699 written=702
        ->  Hash Join  (cost=6811.77..14815.66 rows=147256 width=25) (actual time=45.320..155.184 rows=162016 loops=1)
              Hash Cond: (i.sale_id = s.id)
              Buffers: shared hit=5242
              ->  Seq Scan on sale_items i  (cost=0.00..6842.87 rows=161804 width=32) (actual time=0.005..51.871 rows=162016 loops=1)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Rows Removed by Filter: 16156
                    Buffers: shared hit=2834
              ->  Hash  (cost=5523.12..5523.12 rows=103092 width=29) (actual time=45.256..45.256 rows=102974 loops=1)
                    Buckets: 131072  Batches: 1  Memory Usage: 7165kB
                    Buffers: shared hit=2408
                    ->  Seq Scan on sales s  (cost=0.00..5523.12 rows=103092 width=29) (actual time=0.006..36.815 rows=102974 loops=1)
                          Filter: ((created_at_local >= '2025-11-01 00:00:00+00'::timestamp with time zone) AND (created_at_local < '2026-11-01 00:00:00+00'::timestamp with time zone) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                          Rows Removed by Filter: 10303
                          Buffers: shared hit=2408
Planning:
  Buffers: shared hit=17
Planning Time: 0.173 ms
Execution Time: 254.929 ms
```

### 10. Закрытие смены — самая загруженная смена

Цель ТЗ: < 200 мс · медиана **0.416 мс** (9 прогонов) · первый прогон 0.997 мс · мин 0.401 / макс 0.510 мс · индекс `ix_sales_shift`

**Итог: ПРОЙДЕНО**

```sql
SELECT p.method,
       count(DISTINCT s.id) AS checks,
       sum(CASE WHEN s.type = 'sale' THEN p.amount ELSE -p.amount END) AS amount
FROM sales s
JOIN payments p ON p.sale_id = s.id
WHERE s.shift_id = '01a1af89-7095-7d1d-9f49-b8a819e35df8'
GROUP BY p.method
```

```
GroupAggregate  (cost=1497.58..1499.66 rows=2 width=45) (actual time=0.303..0.317 rows=2 loops=1)
  Group Key: p.method
  Buffers: shared hit=538
  ->  Sort  (cost=1497.58..1497.92 rows=137 width=31) (actual time=0.291..0.296 rows=150 loops=1)
        Sort Key: p.method, s.id
        Sort Method: quicksort  Memory: 33kB
        Buffers: shared hit=538
        ->  Nested Loop  (cost=5.82..1492.72 rows=137 width=31) (actual time=0.007..0.240 rows=150 loops=1)
              Buffers: shared hit=538
              ->  Bitmap Heap Scan on sales s  (cost=5.40..464.78 rows=131 width=21) (actual time=0.005..0.048 rows=132 loops=1)
                    Recheck Cond: (shift_id = '01a1af89-7095-7d1d-9f49-b8a819e35df8'::uuid)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Heap Blocks: exact=8
                    Buffers: shared hit=10
                    ->  Bitmap Index Scan on ix_sales_shift  (cost=0.00..5.37 rows=143 width=0) (actual time=0.003..0.003 rows=132 loops=1)
                          Index Cond: (shift_id = '01a1af89-7095-7d1d-9f49-b8a819e35df8'::uuid)
                          Buffers: shared hit=2
              ->  Index Scan using ix_payments_sale on payments p  (cost=0.42..7.84 rows=1 width=26) (actual time=0.001..0.001 rows=1 loops=132)
                    Index Cond: (sale_id = s.id)
                    Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                    Buffers: shared hit=528
Planning:
  Buffers: shared hit=17
Planning Time: 0.103 ms
Execution Time: 0.329 ms
```

### 11. История движений — самая ходовая пара, вся история с остатком

Цель ТЗ: < 100 мс · медиана **2.104 мс** (9 прогонов) · первый прогон 4.830 мс · мин 2.010 / макс 2.280 мс · индекс `ix_movements_variant`

**Итог: ПРОЙДЕНО**

```sql
SELECT m.occurred_at, m.reason, m.qty, m.store_id, m.doc_id,
       sum(m.qty) OVER (PARTITION BY m.store_id ORDER BY m.occurred_at, m.id) AS balance
FROM stock_movements m
WHERE m.variant_id = '01a1af88-88fb-7008-9008-10680cba226c'
ORDER BY m.occurred_at DESC, m.id DESC
```

```
Sort  (cost=3094.65..3098.72 rows=1627 width=101) (actual time=2.190..2.246 rows=2019 loops=1)
  Sort Key: occurred_at DESC, id DESC
  Sort Method: quicksort  Memory: 238kB
  Buffers: shared hit=2028
  ->  WindowAgg  (cost=2971.26..3007.87 rows=1627 width=101) (actual time=1.009..1.935 rows=2019 loops=1)
        Buffers: shared hit=2028
        ->  Sort  (cost=2971.26..2975.33 rows=1627 width=69) (actual time=1.005..1.069 rows=2019 loops=1)
              Sort Key: store_id, occurred_at, id
              Sort Method: quicksort  Memory: 238kB
              Buffers: shared hit=2028
              ->  Index Scan using ix_movements_variant on stock_movements m  (cost=0.44..2884.48 rows=1627 width=69) (actual time=0.007..0.453 rows=2019 loops=1)
                    Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (variant_id = '01a1af88-88fb-7008-9008-10680cba226c'::uuid))
                    Buffers: shared hit=2028
Planning Time: 0.050 ms
Execution Time: 2.347 ms
```

### 12. История движений — обычная пара

Цель ТЗ: < 100 мс · медиана **0.046 мс** (9 прогонов) · первый прогон 0.113 мс · мин 0.038 / макс 0.161 мс · индекс `ix_movements_variant`

**Итог: ПРОЙДЕНО**

```sql
SELECT m.occurred_at, m.reason, m.qty, m.store_id, m.doc_id,
       sum(m.qty) OVER (PARTITION BY m.store_id ORDER BY m.occurred_at, m.id) AS balance
FROM stock_movements m
WHERE m.variant_id = '01a1af88-8b46-784b-b589-73d24a011f6a'
ORDER BY m.occurred_at DESC, m.id DESC
```

```
Sort  (cost=12.11..12.12 rows=3 width=101) (actual time=0.010..0.010 rows=2 loops=1)
  Sort Key: occurred_at DESC, id DESC
  Sort Method: quicksort  Memory: 25kB
  Buffers: shared hit=5
  ->  WindowAgg  (cost=12.02..12.09 rows=3 width=101) (actual time=0.008..0.009 rows=2 loops=1)
        Buffers: shared hit=5
        ->  Sort  (cost=12.02..12.03 rows=3 width=69) (actual time=0.006..0.006 rows=2 loops=1)
              Sort Key: store_id, occurred_at, id
              Sort Method: quicksort  Memory: 25kB
              Buffers: shared hit=5
              ->  Index Scan using ix_movements_variant on stock_movements m  (cost=0.44..12.00 rows=3 width=69) (actual time=0.004..0.004 rows=2 loops=1)
                    Index Cond: ((tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (variant_id = '01a1af88-8b46-784b-b589-73d24a011f6a'::uuid))
                    Buffers: shared hit=5
Planning Time: 0.032 ms
Execution Time: 0.019 ms
```

### 13. Очередь синхронизации — взять пачку 100

Цель ТЗ: < 10 мс · медиана **0.091 мс** (9 прогонов) · первый прогон 0.369 мс · мин 0.088 / макс 0.118 мс · индекс `ix_outbox_pending`

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
Limit  (cost=0.28..401.53 rows=100 width=106) (actual time=0.004..0.075 rows=100 loops=1)
  Buffers: shared hit=300
  ->  LockRows  (cost=0.28..4217.39 rows=1051 width=106) (actual time=0.004..0.071 rows=100 loops=1)
        Buffers: shared hit=300
        ->  Index Scan using ix_outbox_pending on sync_outbox  (cost=0.28..4206.88 rows=1051 width=106) (actual time=0.004..0.049 rows=100 loops=1)
              Index Cond: (next_attempt_at <= now())
              Filter: ((sent_at IS NULL) AND (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
              Buffers: shared hit=100
Planning Time: 0.027 ms
Execution Time: 0.085 ms
```

### 14. Чек целиком — строки и оплаты одним запросом

Цель ТЗ: нет · медиана **0.103 мс** (9 прогонов) · первый прогон 0.210 мс · мин 0.087 / макс 0.130 мс · индекс `ix_sale_items_sale`

**Итог: цели в ТЗ нет, для справки**

```sql
SELECT s.*,
       (SELECT json_agg(i ORDER BY i.id) FROM sale_items i WHERE i.sale_id = s.id) AS items,
       (SELECT json_agg(p ORDER BY p.id) FROM payments p WHERE p.sale_id = s.id)   AS payments
FROM sales s
WHERE s.id = '01a1af89-70ba-733d-8639-2a3934d05f01'
```

```
Index Scan using sales_pkey on sales s  (cost=0.42..25.44 rows=1 width=222) (actual time=0.021..0.022 rows=1 loops=1)
  Index Cond: (id = '01a1af89-70ba-733d-8639-2a3934d05f01'::uuid)
  Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
  Buffers: shared hit=12
  SubPlan 1
    ->  Aggregate  (cost=8.50..8.51 rows=1 width=32) (actual time=0.013..0.013 rows=1 loops=1)
          Buffers: shared hit=4
          ->  Sort  (cost=8.49..8.50 rows=2 width=144) (actual time=0.005..0.005 rows=4 loops=1)
                Sort Key: i.id
                Sort Method: quicksort  Memory: 25kB
                Buffers: shared hit=4
                ->  Index Scan using ix_sale_items_sale on sale_items i  (cost=0.42..8.48 rows=2 width=144) (actual time=0.002..0.004 rows=4 loops=1)
                      Index Cond: (sale_id = s.id)
                      Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                      Buffers: shared hit=4
  SubPlan 2
    ->  Aggregate  (cost=8.47..8.48 rows=1 width=32) (actual time=0.005..0.005 rows=1 loops=1)
          Buffers: shared hit=4
          ->  Sort  (cost=8.46..8.46 rows=1 width=106) (actual time=0.003..0.003 rows=1 loops=1)
                Sort Key: p.id
                Sort Method: quicksort  Memory: 25kB
                Buffers: shared hit=4
                ->  Index Scan using ix_payments_sale on payments p  (cost=0.42..8.45 rows=1 width=106) (actual time=0.002..0.002 rows=1 loops=1)
                      Index Cond: (sale_id = s.id)
                      Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                      Buffers: shared hit=4
Planning Time: 0.061 ms
Execution Time: 0.033 ms
```

### 15. Накладная целиком — 100 строк одним запросом

Цель ТЗ: нет · медиана **0.217 мс** (9 прогонов) · первый прогон 0.380 мс · мин 0.196 / макс 0.240 мс · индекс `ix_purchase_items_purchase`

**Итог: цели в ТЗ нет, для справки**

```sql
SELECT pu.*,
       (SELECT json_agg(pi ORDER BY pi.id) FROM purchase_items pi WHERE pi.purchase_id = pu.id) AS items
FROM purchases pu
WHERE pu.id = '01a1af88-b35f-7014-b694-4c3aa8b0b72a'
```

```
Index Scan using purchases_pkey on purchases pu  (cost=0.29..394.32 rows=1 width=183) (actual time=0.169..0.170 rows=1 loops=1)
  Index Cond: (id = '01a1af88-b35f-7014-b694-4c3aa8b0b72a'::uuid)
  Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
  Buffers: shared hit=8
  SubPlan 1
    ->  Aggregate  (cost=385.99..386.00 rows=1 width=32) (actual time=0.163..0.164 rows=1 loops=1)
          Buffers: shared hit=5
          ->  Sort  (cost=385.51..385.75 rows=96 width=114) (actual time=0.055..0.058 rows=100 loops=1)
                Sort Key: pi.id
                Sort Method: quicksort  Memory: 38kB
                Buffers: shared hit=5
                ->  Bitmap Heap Scan on purchase_items pi  (cost=5.20..382.35 rows=96 width=114) (actual time=0.006..0.043 rows=100 loops=1)
                      Recheck Cond: (purchase_id = pu.id)
                      Filter: (tenant_id = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)
                      Heap Blocks: exact=2
                      Buffers: shared hit=5
                      ->  Bitmap Index Scan on ix_purchase_items_purchase  (cost=0.00..5.18 rows=100 width=0) (actual time=0.003..0.003 rows=100 loops=1)
                            Index Cond: (purchase_id = pu.id)
                            Buffers: shared hit=3
Planning Time: 0.091 ms
Execution Time: 0.182 ms
```

## Стоимость триггеров

Вставка 10 000 строк в `stock_movements`: в T1–T6 каждая — отдельный `INSERT` (как строка чека), в T7–T8 — пачками по 1 000 строк одним `INSERT ... SELECT` (как импорт или инвентаризация). Время снято по пачкам из 1 000 строк. Рост = (среднее за последние 3 пачки) / (среднее за первые 3). База для сравнения — копия журнала с теми же данными, индексами, FK и RLS, но без триггера `movement_to_stock`. В T5/T6 `synchronous_commit = off`, чтобы мерить триггер, а не fsync диска.

| Код | Сценарий | Строк | Всего, мс | мс/строку | Первые 3 пачки, мс/строку | Последние 3 пачки, мс/строку | Рост | Время растёт? |
|---|---|--:|--:|--:|--:|--:|--:|---|
| T1 | Без триггера (копия журнала): 10 000 разных пар, одна транзакция | 10000 | 286 | 0.0286 | 0.0274 | 0.0266 | ×0.97 | нет |
| T2 | С триггером: 10 000 разных пар, строк в stock ещё нет (INSERT), одна транзакция | 10000 | 482 | 0.0482 | 0.0487 | 0.0484 | ×0.99 | нет |
| T3 | С триггером: те же 10 000 пар, строки в stock есть (UPDATE), одна транзакция | 10000 | 452 | 0.0452 | 0.0446 | 0.0444 | ×1.00 | нет |
| T4 | С триггером: одна пара 10 000 раз, одна транзакция | 10000 | 2372 | 0.2372 | 0.0965 | 0.3984 | ×4.13 | **да** |
| T5 | Без триггера (копия журнала): одна пара 10 000 раз, COMMIT на каждую строку | 10000 | 591 | 0.0591 | 0.0622 | 0.0561 | ×0.90 | нет |
| T6 | С триггером: одна пара 10 000 раз, COMMIT на каждую строку | 10000 | 909 | 0.0909 | 0.0852 | 0.1049 | ×1.23 | нет |
| T7 | С триггером: одна пара 10 000 раз, пачками по 1 000 (INSERT ... SELECT), одна транзакция | 10000 | 126 | 0.0126 | 0.0125 | 0.0126 | ×1.01 | нет |
| T8 | С триггером: 10 000 разных пар, пачками по 1 000 (INSERT ... SELECT), одна транзакция | 10000 | 227 | 0.0227 | 0.0242 | 0.0205 | ×0.84 | нет |

Цена триггера `movement_to_stock` (разница с копией без триггера):

- новая строка остатка (INSERT в stock): **0.0196 мс/строку** (T2 − T1)
- существующая строка остатка (UPDATE stock): **0.0166 мс/строку** (T3 − T1)
- одна и та же пара, коммит на строку: **0.0318 мс/строку** (T6 − T5)
- одна и та же пара 10 000 раз в одной транзакции, по строке: **0.2086 мс/строку** (T4 − T1)
- одна и та же пара 10 000 раз в одной транзакции, пачками: **0.0126 мс/строку** всего (T7) — в 18.9 раз быстрее T4
- 10 000 разных пар пачками: **0.0227 мс/строку** всего (T8) против 0.0452 по строке (T3)

### По пачкам, мс на строку

| Код | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| T1 | 0.0286 | 0.0267 | 0.0268 | 0.0307 | 0.0267 | 0.0346 | 0.0320 | 0.0263 | 0.0271 | 0.0265 |
| T2 | 0.0492 | 0.0483 | 0.0485 | 0.0472 | 0.0483 | 0.0482 | 0.0476 | 0.0469 | 0.0500 | 0.0482 |
| T3 | 0.0438 | 0.0436 | 0.0464 | 0.0434 | 0.0439 | 0.0448 | 0.0522 | 0.0434 | 0.0444 | 0.0454 |
| T4 | 0.0654 | 0.0974 | 0.1266 | 0.1749 | 0.2110 | 0.2322 | 0.2699 | 0.3315 | 0.3954 | 0.4683 |
| T5 | 0.0546 | 0.0744 | 0.0578 | 0.0586 | 0.0553 | 0.0654 | 0.0572 | 0.0555 | 0.0574 | 0.0553 |
| T6 | 0.0906 | 0.0885 | 0.0766 | 0.0840 | 0.0822 | 0.0800 | 0.0923 | 0.1450 | 0.0813 | 0.0885 |
| T7 | 0.0127 | 0.0125 | 0.0124 | 0.0125 | 0.0126 | 0.0125 | 0.0126 | 0.0125 | 0.0126 | 0.0129 |
| T8 | 0.0215 | 0.0235 | 0.0277 | 0.0220 | 0.0206 | 0.0257 | 0.0251 | 0.0216 | 0.0200 | 0.0198 |

## Параметры замера

| Ключ | Значение |
|---|---|
| bc | `4600000250001` |
| bc_missing | `0000000000000` |
| cat | `01a1af88-840e-73f7-a893-b6956c880ab3` |
| period_from | `2025-11-01 00:00:00+00` |
| period_to | `2026-11-01 00:00:00+00` |
| purchase_doc | `01a1af88-b35f-7014-b694-4c3aa8b0b72a` |
| q_freq | `кроссовки` |
| q_rare | `кроссовки runner` |
| q_short | `run` |
| sale_doc | `01a1af89-70ba-733d-8639-2a3934d05f01` |
| shift | `01a1af89-7095-7d1d-9f49-b8a819e35df8` |
| store | `01a1af88-838f-7d03-8881-5057ae5481c8` |
| trig_store | `01a1af89-d7a1-7722-bff0-12510fdd58ce` |
| variant_hot | `01a1af88-88fb-7008-9008-10680cba226c` |
| variant_typ | `01a1af88-8b46-784b-b589-73d24a011f6a` |

