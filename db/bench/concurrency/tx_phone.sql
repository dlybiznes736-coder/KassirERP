-- Телефон сотрудника заводит товар: модель, 10 пар (5 размеров × 2 цвета)
-- со штрихкодами, 3 фото (только storage_key), накладная на 10 строк и её
-- приёмка — остатки появляются в том же филиале, где идут продажи.
-- Штрихкоды '29' + 11 цифр, не пересекаются с генератором ('46', '47').
BEGIN;
SELECT set_config('app.tenant_id', ':tenant', true);
SELECT gen_random_uuid() AS pid, uuid_v7() AS puid, nextval('bench_conc.product_no') AS pno \gset
INSERT INTO products (id, tenant_id, name, brand, article)
VALUES (':pid', ':tenant', 'Залив с телефона ' || :pno, 'Тест', 'PH-' || :pno);
INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode, cost_price, sale_price)
SELECT gen_random_uuid(), ':tenant', ':pid', (38 + s)::text, c.name,
       '29' || lpad((:pno * 10 + s * 2 + c.k)::text, 11, '0'), 2500.00, 4990.00
FROM generate_series(0, 4) s
CROSS JOIN (VALUES (0, 'чёрный'), (1, 'белый')) c(k, name);
INSERT INTO product_photos (id, tenant_id, product_id, storage_key, sort_order)
SELECT gen_random_uuid(), ':tenant', ':pid', ':tenant/:pid/' || gen_random_uuid() || '.jpg', n
FROM generate_series(1, 3) n;
INSERT INTO purchases (id, tenant_id, store_id, total) VALUES (':puid', ':tenant', ':store', 125000.00);
INSERT INTO purchase_items (id, tenant_id, purchase_id, variant_id, qty, cost_price)
SELECT gen_random_uuid(), ':tenant', ':puid', v.id, 5, 2500.00
FROM product_variants v WHERE v.product_id = ':pid';
UPDATE purchases SET status = 'accepted' WHERE id = ':puid';
COMMIT;
