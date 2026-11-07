-- =====================================================================
-- Замер скана с прайс-листом (партия A): лист на все 500 000 пар основного
-- тенанта генератора, цена +10 к базовой, назначен «Филиалу 1» стенда.
--
-- Назначить (около 20 с, вместе с журналом цен):
--   docker compose exec -T postgres psql -U postgres -d kassir -X -v ON_ERROR_STOP=1 -f /db/bench/scan_price_list.sql
-- Снять (стенд вернётся на базовые цены; лист останется):
--   docker compose exec -T postgres psql -U postgres -d kassir -X -c "UPDATE stores SET price_list_id = NULL WHERE price_list_id = 'b0000000-0000-0000-0000-00000000be10'"
-- =====================================================================
\set ON_ERROR_STOP on

INSERT INTO price_lists (id, tenant_id, name)
VALUES ('b0000000-0000-0000-0000-00000000be10', 'c0000000-0000-0000-0000-000000000001', 'Замер: все пары')
ON CONFLICT DO NOTHING;

INSERT INTO price_list_items (tenant_id, price_list_id, variant_id, sale_price)
SELECT tenant_id, 'b0000000-0000-0000-0000-00000000be10', id, sale_price + 10
FROM product_variants
WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001' AND barcode LIKE '46%'
ON CONFLICT DO NOTHING;

UPDATE stores SET price_list_id = 'b0000000-0000-0000-0000-00000000be10'
WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001' AND name = 'Филиал 1';

ANALYZE price_list_items;
SELECT count(*) AS цен_в_листе FROM price_list_items WHERE price_list_id = 'b0000000-0000-0000-0000-00000000be10';
