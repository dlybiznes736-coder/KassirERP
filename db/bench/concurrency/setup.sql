-- =====================================================================
-- Kassir ERP — конкурентный тест (шаг 4б): подготовка.
-- Вызывается из run.sh от суперпользователя. Нужны данные generate.sql.
--
-- Всё служебное — в схеме bench_conc (не bench: её пересоздаёт bench.sql,
-- а последовательность номеров чеков должна переживать повторные прогоны,
-- иначе номера на кассах bench-kassa-N начнут повторяться).
-- =====================================================================
\set ON_ERROR_STOP on

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM tenants WHERE id = 'c0000000-0000-0000-0000-000000000001') THEN
    RAISE EXCEPTION 'Нет данных: сначала запустите /db/bench/generate.sql';
  END IF;
END $$;

CREATE SCHEMA IF NOT EXISTS bench_conc;
CREATE SEQUENCE IF NOT EXISTS bench_conc.check_no;    -- номер чека на кассе
CREATE SEQUENCE IF NOT EXISTS bench_conc.product_no;  -- номер товара с телефона → штрихкоды

-- Результаты каждого полного прогона — с нуля.
DROP TABLE IF EXISTS bench_conc.params, bench_conc.runs, bench_conc.jobs,
                     bench_conc.lat, bench_conc.lock_samples;

CREATE TABLE bench_conc.params (k text PRIMARY KEY, v text NOT NULL);

CREATE TABLE bench_conc.runs (
  code             text PRIMARY KEY,
  ord              int NOT NULL,
  descr            text NOT NULL,
  seconds          int NOT NULL,
  started          timestamptz NOT NULL,
  finished         timestamptz,
  deadlocks_before bigint NOT NULL,
  deadlocks_after  bigint,
  stock_ok         boolean
);

-- Один поток нагрузки внутри сценария (кассиры / отчёты / телефоны).
CREATE TABLE bench_conc.jobs (
  code     text NOT NULL,
  job      text NOT NULL,
  clients  int NOT NULL,
  script   text NOT NULL,
  output   text NOT NULL,       -- вывод pgbench целиком
  PRIMARY KEY (code, job)
);

-- Каждая транзакция из логов pgbench (-l).
CREATE TABLE bench_conc.lat (
  code    text NOT NULL,
  job     text NOT NULL,
  client  int NOT NULL,
  status  text NOT NULL,        -- ok | failed | serialization | deadlock
  ms      numeric               -- NULL для неуспешных
);

-- Снимки ожиданий на блокировках (pg_locks, granted = false) раз в ~20 мс.
CREATE TABLE bench_conc.lock_samples (
  code         text NOT NULL,
  ts           timestamptz NOT NULL,
  waiting      int NOT NULL,    -- сколько процессов ждут блокировку
  w_tuple      int NOT NULL,    -- ждут строку (tuple)
  w_txid       int NOT NULL,    -- ждут чужую транзакцию (transactionid)
  w_other      int NOT NULL,
  max_wait_ms  numeric NOT NULL -- самое долгое текущее ожидание (pg_locks.waitstart)
);

-- ---------------------------------------------------------------------
-- Параметры: тенант, филиал, кассир, ходовая пара
-- Все кассы садятся в ОДИН филиал — худший случай для блокировок stock.
-- ---------------------------------------------------------------------
INSERT INTO bench_conc.params VALUES ('tenant', 'c0000000-0000-0000-0000-000000000001');

INSERT INTO bench_conc.params
SELECT 'store', store_id::text FROM sales
WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001'
GROUP BY store_id ORDER BY count(*) DESC, store_id LIMIT 1;

INSERT INTO bench_conc.params
SELECT 'cashier', id::text FROM users
WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001' AND role = 'cashier'
ORDER BY login LIMIT 1;

-- Штрихкоды генератора: '46' || lpad(n, 11, '0'), n = 1..nvar
INSERT INTO bench_conc.params
SELECT 'nvar', count(*)::text FROM product_variants
WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001' AND barcode LIKE '46%';

-- Ходовая пара: больше всего движений за историю
INSERT INTO bench_conc.params
SELECT 'hotn', substr(v.barcode, 3)::int::text
FROM (SELECT variant_id FROM stock_movements
      WHERE tenant_id = 'c0000000-0000-0000-0000-000000000001'
      GROUP BY variant_id ORDER BY count(*) DESC, variant_id LIMIT 1) m
JOIN product_variants v ON v.id = m.variant_id;

-- ---------------------------------------------------------------------
-- Кассы bench-kassa-0..99 и их смены, id детерминированные:
-- md5('bench-device-' || client_id)::uuid и md5('bench-shift-' || client_id)::uuid —
-- скрипт кассы считает их сам. Секрета у касс нет: pgbench пишет чеки напрямую.
-- ---------------------------------------------------------------------
INSERT INTO devices (id, tenant_id, store_id, name, secret_hash)
SELECT md5('bench-device-' || i)::uuid, 'c0000000-0000-0000-0000-000000000001',
       (SELECT v::uuid FROM bench_conc.params WHERE k = 'store'), 'bench-kassa-' || i, 'none'
FROM generate_series(0, 99) i
ON CONFLICT (id) DO NOTHING;

INSERT INTO cash_shifts (id, tenant_id, store_id, device_id, cashier_id, status, opened_at)
SELECT md5('bench-shift-' || i)::uuid, 'c0000000-0000-0000-0000-000000000001',
       (SELECT v::uuid FROM bench_conc.params WHERE k = 'store'),
       md5('bench-device-' || i)::uuid,
       (SELECT v::uuid FROM bench_conc.params WHERE k = 'cashier'),
       'open', now()
FROM generate_series(0, 99) i
ON CONFLICT (id) DO NOTHING;

GRANT USAGE ON SCHEMA bench_conc TO kassir_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA bench_conc TO kassir_app;

-- ---------------------------------------------------------------------
-- Процедуры прогона
-- ---------------------------------------------------------------------
CREATE OR REPLACE PROCEDURE bench_conc.begin_run(p_code text, p_ord int, p_descr text, p_seconds int)
LANGUAGE sql AS $$
  INSERT INTO bench_conc.runs (code, ord, descr, seconds, started, deadlocks_before)
  SELECT p_code, p_ord, p_descr, p_seconds, clock_timestamp(), deadlocks
  FROM pg_stat_database WHERE datname = current_database();
$$;

-- Снимает ожидания на блокировках, пока идёт сценарий. COMMIT на каждый
-- снимок, чтобы не держать транзакцию открытой.
CREATE OR REPLACE PROCEDURE bench_conc.sample_locks(p_code text, p_seconds numeric, p_interval numeric DEFAULT 0.02)
LANGUAGE plpgsql AS $$
DECLARE t_end timestamptz := clock_timestamp() + make_interval(secs => p_seconds);
BEGIN
  WHILE clock_timestamp() < t_end LOOP
    INSERT INTO bench_conc.lock_samples
    SELECT p_code, clock_timestamp(), count(*),
           count(*) FILTER (WHERE l.locktype = 'tuple'),
           count(*) FILTER (WHERE l.locktype = 'transactionid'),
           count(*) FILTER (WHERE l.locktype NOT IN ('tuple', 'transactionid')),
           coalesce(max(extract(epoch FROM clock_timestamp() - l.waitstart) * 1000), 0)
    FROM pg_locks l
    WHERE NOT l.granted
      AND l.pid IN (SELECT pid FROM pg_stat_activity WHERE datname = current_database());
    COMMIT;
    PERFORM pg_sleep(p_interval);
  END LOOP;
END $$;

CREATE OR REPLACE PROCEDURE bench_conc.end_run(p_code text)
LANGUAGE plpgsql AS $$
BEGIN
  -- Счётчики pg_stat_database сбрасываются в общую память с задержкой до 1 с.
  PERFORM pg_sleep(1.5);
  COMMIT;
  UPDATE bench_conc.runs r
     SET finished = clock_timestamp(),
         deadlocks_after = (SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()),
         stock_ok = NOT EXISTS (
           SELECT 1 FROM stock s
           FULL JOIN (SELECT tenant_id, store_id, variant_id, sum(qty) q
                        FROM stock_movements GROUP BY 1, 2, 3) m
             USING (tenant_id, store_id, variant_id)
           WHERE s.qty IS DISTINCT FROM m.q)
   WHERE r.code = p_code;
END $$;

-- Загрузка лога pgbench: строки «client tx_no time script epoch us»,
-- time — микросекунды или failed / serialization / deadlock.
CREATE OR REPLACE PROCEDURE bench_conc.load_job(p_code text, p_job text, p_clients int,
                                                p_script text, p_log text, p_out text)
LANGUAGE plpgsql AS $$
BEGIN
  CREATE TEMP TABLE IF NOT EXISTS r (line text);
  TRUNCATE r;
  EXECUTE format('COPY r FROM %L', p_log);
  INSERT INTO bench_conc.lat (code, job, client, status, ms)
  SELECT p_code, p_job, split_part(line, ' ', 1)::int,
         CASE WHEN split_part(line, ' ', 3) ~ '^[0-9]+$' THEN 'ok' ELSE split_part(line, ' ', 3) END,
         CASE WHEN split_part(line, ' ', 3) ~ '^[0-9]+$' THEN split_part(line, ' ', 3)::numeric / 1000 END
  FROM r WHERE line <> '';
  INSERT INTO bench_conc.jobs VALUES (p_code, p_job, p_clients, p_script, pg_read_file(p_out));
END $$;
