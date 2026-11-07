-- Быстрая проверка принципов схемы. Всё в транзакции с ROLLBACK — база не меняется.
-- Запуск: docker compose exec -T postgres psql -U postgres -d kassir -v ON_ERROR_STOP=1 -f /db/tests/smoke.sql
\set ON_ERROR_STOP on
BEGIN;

-- Данные чужого тенанта B — от суперпользователя (он обходит RLS).
INSERT INTO tenants (id, name) VALUES
  ('a0000000-0000-0000-0000-000000000001', 'Магазин A'),
  ('b0000000-0000-0000-0000-000000000001', 'Магазин B');
INSERT INTO products (id, tenant_id, name) VALUES
  ('b0000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000001', 'Чужой товар');

-- Дальше — как приложение: роль kassir_app, тенант A.
SET LOCAL ROLE kassir_app;
SET LOCAL app.tenant_id = 'a0000000-0000-0000-0000-000000000001';

DO $$
DECLARE
  t   uuid := 'a0000000-0000-0000-0000-000000000001';
  st  uuid := 'a0000000-0000-0000-0000-000000000002';
  usr uuid := 'a0000000-0000-0000-0000-000000000003';
  pr  uuid := 'a0000000-0000-0000-0000-000000000004';
  v   uuid := 'a0000000-0000-0000-0000-000000000005';
  pu  uuid := 'a0000000-0000-0000-0000-000000000007';
  pu2 uuid := 'a0000000-0000-0000-0000-000000000008';
  sh  uuid := 'a0000000-0000-0000-0000-000000000009';
  sa  uuid := 'a0000000-0000-0000-0000-000000000010';
  si  uuid := 'a0000000-0000-0000-0000-000000000011';
  ret uuid := 'a0000000-0000-0000-0000-000000000020';
  n   numeric;
  ok  boolean;
BEGIN
  INSERT INTO stores (id, tenant_id, name) VALUES (st, t, 'Центр');
  INSERT INTO users (id, tenant_id, full_name, login, role) VALUES (usr, t, 'Кассир', 'kassa1', 'cashier');
  INSERT INTO products (id, tenant_id, name, brand) VALUES (pr, t, 'Кроссовки Runner', 'Nike');
  INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode, cost_price, sale_price)
    VALUES (v, t, pr, '42', 'чёрный', '4600000000012', 2500.00, 4990.00);

  -- 1. Приход: остаток меняется только при accepted
  INSERT INTO purchases (id, tenant_id, store_id) VALUES (pu, t, st);
  INSERT INTO purchase_items (id, tenant_id, purchase_id, variant_id, qty, cost_price)
    VALUES (uuid_v7(), t, pu, v, 10, 2500.00);
  ASSERT NOT EXISTS (SELECT 1 FROM stock WHERE variant_id = v), 'остаток появился до приёмки';
  UPDATE purchases SET status = 'accepted', accepted_by = usr WHERE id = pu;
  SELECT qty INTO n FROM stock WHERE store_id = st AND variant_id = v;
  ASSERT n = 10, format('после прихода ждали 10, получили %s', n);
  RAISE NOTICE 'OK  приход: остаток 10';

  -- 2. Продажа 2 пар: две строки одной пары одним INSERT (как чек с кассы) —
  --    триггер обязан свернуть их в одно обновление stock
  INSERT INTO cash_shifts (id, tenant_id, store_id, device_id, cashier_id, opened_at)
    VALUES (sh, t, st, 'kassa-1', usr, now());
  INSERT INTO sales (id, tenant_id, store_id, shift_id, cashier_id, device_id, local_number,
                     subtotal, total, created_at_local)
    VALUES (sa, t, st, sh, usr, 'kassa-1', 1, 9980.00, 9980.00, now());
  INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, qty, price, cost_price, total)
    VALUES (si,        t, sa, v, 1, 4990.00, 2500.00, 4990.00),
           (uuid_v7(), t, sa, v, 1, 4990.00, 2500.00, 4990.00);
  ASSERT (SELECT count(*) FROM stock_movements WHERE doc_id = sa) = 2, 'ждали 2 движения по чеку';
  INSERT INTO payments (id, tenant_id, sale_id, method, amount) VALUES
    (uuid_v7(), t, sa, 'cash', 5000.00), (uuid_v7(), t, sa, 'card', 4980.00);
  SELECT qty INTO n FROM stock WHERE store_id = st AND variant_id = v;
  ASSERT n = 8, format('после продажи ждали 8, получили %s', n);
  RAISE NOTICE 'OK  продажа: остаток 8';

  -- 3. Идемпотентность: тот же чек второй раз
  ok := false;
  BEGIN
    INSERT INTO sales (id, tenant_id, store_id, shift_id, cashier_id, device_id, local_number,
                       subtotal, total, created_at_local)
      VALUES (sa, t, st, sh, usr, 'kassa-1', 1, 9980.00, 9980.00, now());
  EXCEPTION WHEN unique_violation THEN ok := true;
  END;
  ASSERT ok, 'повторный чек не отклонён';
  ASSERT (SELECT count(*) FROM sales WHERE id = sa) = 1;
  RAISE NOTICE 'OK  повторная отправка чека отклонена';

  -- 4. Цена товара меняется — история продаж нет
  UPDATE product_variants SET sale_price = 5990.00 WHERE id = v;
  ASSERT (SELECT price FROM sale_items WHERE id = si) = 4990.00;
  RAISE NOTICE 'OK  смена цены не трогает проданный чек';

  -- 5. Возврат 1 пары
  INSERT INTO sales (id, tenant_id, store_id, shift_id, cashier_id, device_id, local_number,
                     type, return_of_sale_id, subtotal, total, created_at_local)
    VALUES (ret, t, st, sh, usr, 'kassa-1', 2, 'return', sa, 4990.00, 4990.00, now());
  INSERT INTO sale_items (id, tenant_id, sale_id, variant_id, return_of_item_id, qty, price, cost_price, total)
    VALUES (uuid_v7(), t, ret, v, si, 1, 4990.00, 2500.00, 4990.00);
  SELECT qty INTO n FROM stock WHERE store_id = st AND variant_id = v;
  ASSERT n = 9, format('после возврата ждали 9, получили %s', n);
  RAISE NOTICE 'OK  возврат: остаток 9';

  -- 5a. Пакет: 1 000 движений одной пары одним оператором
  INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason, occurred_at)
  SELECT uuid_v7(), t, st, v, 1, 'adjustment', now() FROM generate_series(1, 1000);
  SELECT qty INTO n FROM stock WHERE store_id = st AND variant_id = v;
  ASSERT n = 1009, format('после пакета ждали 1009, получили %s', n);
  RAISE NOTICE 'OK  пакет из 1 000 движений одним оператором: остаток 1009';

  -- 5b. Черновик накладной удаляется, принятая — нет
  INSERT INTO purchases (id, tenant_id, store_id) VALUES (pu2, t, st);
  DELETE FROM purchases WHERE id = pu2;
  ASSERT NOT EXISTS (SELECT 1 FROM purchases WHERE id = pu2), 'черновик накладной не удалился';
  ok := false;
  BEGIN DELETE FROM purchases WHERE id = pu;
  EXCEPTION WHEN restrict_violation THEN ok := true; END;
  ASSERT ok, 'принятая накладная удалилась';
  RAISE NOTICE 'OK  черновик накладной удаляется, принятая — нет';

  -- 6. stock == SUM(stock_movements)
  ASSERT NOT EXISTS (
    SELECT 1 FROM stock s
    FULL JOIN (SELECT store_id, variant_id, sum(qty) q FROM stock_movements GROUP BY 1, 2) m
      USING (store_id, variant_id)
    WHERE s.qty IS DISTINCT FROM m.q
  ), 'stock расходится с журналом';
  RAISE NOTICE 'OK  stock совпадает с суммой движений';

  -- 7. Журнал и кэш нельзя править руками
  ok := false;
  BEGIN UPDATE stock_movements SET qty = 100;
  EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
  ASSERT ok, 'UPDATE stock_movements не запрещён';
  ok := false;
  BEGIN UPDATE stock SET qty = 100;
  EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
  ASSERT ok, 'UPDATE stock не запрещён';
  RAISE NOTICE 'OK  журнал и кэш остатков руками не правятся';

  -- 8. RLS: чужие строки не видны и не пишутся
  ASSERT (SELECT count(*) FROM products) = 1, 'видны чужие товары';
  ASSERT (SELECT count(*) FROM tenants) = 1, 'видны чужие тенанты';
  ok := false;
  BEGIN
    INSERT INTO products (id, tenant_id, name)
      VALUES (uuid_v7(), 'b0000000-0000-0000-0000-000000000001', 'взлом');
  EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
  ASSERT ok, 'удалось записать в чужой тенант';
  RAISE NOTICE 'OK  RLS: чужой тенант не виден и не пишется';

  -- 9. Поиск (миграция 0003): функция обходит RLS, поэтому проверяем её отдельно
  IF to_regprocedure('catalog.search_products(text,integer)') IS NOT NULL THEN
    ASSERT (SELECT count(*) FROM catalog.search_products('Runner', 50)) = 1, 'свой товар не найден';
    ASSERT (SELECT count(*) FROM catalog.search_products('товар', 50)) = 0, 'поиск отдал товар чужого тенанта';
    ASSERT (SELECT count(*) FROM catalog.search_products('%%%', 50)) = 0, '%%% в запросе сработал как шаблон';
    PERFORM set_config('app.tenant_id', '', true);
    ASSERT (SELECT count(*) FROM catalog.search_products('Runner', 50)) = 0, 'без тенанта поиск что-то вернул';
    PERFORM set_config('app.tenant_id', t::text, true);
    RAISE NOTICE 'OK  поиск: только свой тенант, без тенанта — пусто, %% буквально';
  END IF;
END $$;

ROLLBACK;
\echo 'Все проверки пройдены'
