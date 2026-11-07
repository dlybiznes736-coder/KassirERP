-- =====================================================================
-- Стенд для ручной проверки и замеров API в данных генератора.
-- Только генератор (не миграции) создаёт это — в рабочей базе стенда нет.
-- Вызывается из generate.sql; на уже сгенерированную базу — повторно и безопасно:
--   docker compose exec -T postgres psql -U postgres -d kassir -X -v ON_ERROR_STOP=1 \
--     -v tenant=c0000000-0000-0000-0000-000000000001 -f /db/bench/_bench_stand.sql
--
-- - фирма с кодом «bench» (основной тенант генератора);
-- - менеджер «bench», пароль bench-password-1, филиал 1: вход по паролю,
--   выдаёт коды подключения касс, принимает товар;
-- - у всех кассиров основного тенанта (cashier1_1, cashier1_2, … — филиал 1,
--   cashier2_1 … — филиал 2) PIN 1234;
-- - весовой товар «Колбаса докторская (стенд)», PLU 01234, 520.00 за кг,
--   остаток 50 кг в филиале 1: штрихкод 2201234012502 = 1.250 кг.
-- Хеши — PBKDF2-SHA512 (210 000 итераций) с фиксированной солью в формате
-- Pbkdf2PasswordHasher. Пароль и PIN известны: только для стенда.
-- =====================================================================
\set ON_ERROR_STOP on

UPDATE tenants SET code = 'bench' WHERE id = :'tenant';

INSERT INTO users (id, tenant_id, store_id, full_name, login, role, password_hash)
SELECT 'b0000000-0000-0000-0000-00000000be01', :'tenant', s.id, 'Менеджер стенда', 'bench', 'manager',
       'pbkdf2-sha512$210000$a2Fzc2lyLWJlbmNoLXYxIQ==$FoCyvr0+4QpEQUonMRT4kHQlRU9Fup/90CPFvubUVAU='
FROM stores s WHERE s.tenant_id = :'tenant' AND s.name = 'Филиал 1'
ON CONFLICT (id) DO UPDATE SET role = 'manager', store_id = EXCLUDED.store_id, password_hash = EXCLUDED.password_hash;

UPDATE users SET pin_hash = 'pbkdf2-sha512$210000$a2Fzc2lyLWJlbmNoLXBpbg==$lSi0PR5G6dutdMm+TwoEyy0Ds9083SJ31tlu51LztUs='
WHERE tenant_id = :'tenant' AND role = 'cashier';

INSERT INTO products (id, tenant_id, name, brand)
VALUES ('b0000000-0000-0000-0000-00000000be02', :'tenant', 'Колбаса докторская (стенд)', 'Стенд')
ON CONFLICT (id) DO NOTHING;

INSERT INTO product_variants (id, tenant_id, product_id, unit, plu, cost_price, sale_price)
VALUES ('b0000000-0000-0000-0000-00000000be03', :'tenant', 'b0000000-0000-0000-0000-00000000be02', 'kg', '01234', 300.00, 520.00)
ON CONFLICT (id) DO NOTHING;

-- Остаток — штатно, через журнал; один раз (doc_line_id уникален для reason).
INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason, doc_line_id, occurred_at)
SELECT 'b0000000-0000-0000-0000-00000000be04', :'tenant', s.id, 'b0000000-0000-0000-0000-00000000be03', 50, 'adjustment',
       'b0000000-0000-0000-0000-00000000be04', now()
FROM stores s WHERE s.tenant_id = :'tenant' AND s.name = 'Филиал 1'
ON CONFLICT DO NOTHING;
