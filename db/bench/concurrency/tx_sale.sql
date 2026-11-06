-- Один чек, принятый сервером от кассы: скан двух штрихкодов, чек,
-- оплата, две строки одним INSERT. Роль kassir_app, RLS включён.
-- Каждый клиент pgbench — своя касса bench-kassa-<client_id> со своей сменой.
--
-- -D hot=1        первая строка каждого чека — ходовая пара (все кассы бьют
--                 в одну строку stock), вторая — случайная.
-- -D items_last=1 строки чека — последний оператор перед COMMIT (так должен
--                 делать бэкенд): строка stock блокируется триггером только
--                 на время COMMIT. items_last=0 — строки до оплаты, для сравнения.
\if :hot
\set n1 :hotn
\else
\set n1 random(1, :nvar)
\endif
\set n2 random(1, :nvar)
BEGIN;
SELECT set_config('app.tenant_id', ':tenant', true);
SELECT gen_random_uuid() AS sid, nextval('bench_conc.check_no') AS no,
       sum(v.sale_price) AS total, sum(coalesce(s.qty, 0)) AS on_hand
FROM (VALUES ('46' || lpad((:n1)::text, 11, '0')), ('46' || lpad((:n2)::text, 11, '0'))) x(bc)
JOIN product_variants v ON v.barcode = x.bc AND v.deleted_at IS NULL AND v.tenant_id = ':tenant'
LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = ':store' AND s.variant_id = v.id \gset
INSERT INTO sales (id, tenant_id, store_id, shift_id, cashier_id, device_id, local_number,
                   subtotal, total, created_at_local)
VALUES (':sid', ':tenant', ':store', md5('bench-shift-' || :client_id)::uuid, ':cashier',
        'bench-kassa-' || :client_id, :no, :total, :total, now());
\if :items_last
INSERT INTO payments (id, tenant_id, sale_id, method, amount)
VALUES (gen_random_uuid(), ':tenant', ':sid', 'cash', :total);
INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, qty, price, cost_price, total)
SELECT gen_random_uuid(), v.tenant_id, ':sid', v.id, 1, v.sale_price, v.cost_price, v.sale_price
FROM (VALUES ('46' || lpad((:n1)::text, 11, '0')), ('46' || lpad((:n2)::text, 11, '0'))) x(bc)
JOIN product_variants v ON v.barcode = x.bc AND v.deleted_at IS NULL AND v.tenant_id = ':tenant';
\else
INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, qty, price, cost_price, total)
SELECT gen_random_uuid(), v.tenant_id, ':sid', v.id, 1, v.sale_price, v.cost_price, v.sale_price
FROM (VALUES ('46' || lpad((:n1)::text, 11, '0')), ('46' || lpad((:n2)::text, 11, '0'))) x(bc)
JOIN product_variants v ON v.barcode = x.bc AND v.deleted_at IS NULL AND v.tenant_id = ':tenant';
INSERT INTO payments (id, tenant_id, sale_id, method, amount)
VALUES (gen_random_uuid(), ':tenant', ':sid', 'cash', :total);
\endif
COMMIT;
