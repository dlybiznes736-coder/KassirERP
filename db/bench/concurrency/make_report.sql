-- Сборка отчёта конкурентного теста. Вызывается из run.sh:
--   psql -v nproc=<ядер> -v report=<файл> -f make_report.sql
\set ON_ERROR_STOP on
\pset format unaligned
\pset tuples_only on
\pset footer off
\pset pager off
\o :report

-- Сводка по потокам: одна строка на (сценарий, поток)
CREATE TEMP TABLE j AS
SELECT r.ord, r.code, r.descr, r.seconds, jb.job, jb.clients,
       count(*) FILTER (WHERE l.status = 'ok')                       AS ok,
       count(*) FILTER (WHERE l.status <> 'ok')                      AS failed,
       count(*) FILTER (WHERE l.status = 'deadlock')                 AS failed_deadlock,
       count(*) FILTER (WHERE l.status = 'serialization')            AS failed_serial,
       regexp_count(jb.output, 'client \d+ aborted')                 AS aborted,
       round(count(*) FILTER (WHERE l.status = 'ok')::numeric / r.seconds, 1) AS tps,
       round(avg(l.ms), 2)                                           AS avg_ms,
       round(percentile_cont(0.50) WITHIN GROUP (ORDER BY l.ms)::numeric, 2) AS p50,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY l.ms)::numeric, 2) AS p95,
       round(percentile_cont(0.99) WITHIN GROUP (ORDER BY l.ms)::numeric, 2) AS p99,
       round(max(l.ms), 2)                                           AS max_ms
FROM bench_conc.runs r
JOIN bench_conc.jobs jb ON jb.code = r.code
LEFT JOIN bench_conc.lat l ON l.code = jb.code AND l.job = jb.job
GROUP BY r.ord, r.code, r.descr, r.seconds, jb.job, jb.clients, jb.output;

-- Ожидания на блокировках: суммарное время ≈ Σ (ждущих × интервал снимка)
CREATE TEMP TABLE lk AS
SELECT code,
       count(*)                                                  AS samples,
       round(100.0 * count(*) FILTER (WHERE waiting > 0) / nullif(count(*), 0), 1) AS pct_with_wait,
       round(avg(waiting), 2)                                    AS avg_waiting,
       max(waiting)                                              AS max_waiting,
       round(sum(waiting * dt), 2)                               AS wait_s,
       round(sum(w_tuple * dt), 2)                               AS wait_tuple_s,
       round(sum(w_txid * dt), 2)                                AS wait_txid_s,
       round(sum(w_other * dt), 2)                               AS wait_other_s,
       round(max(max_wait_ms), 1)                                AS longest_ms
FROM (SELECT *, coalesce(extract(epoch FROM ts - lag(ts) OVER (PARTITION BY code ORDER BY ts)), 0) AS dt
      FROM bench_conc.lock_samples) s
GROUP BY code;

CREATE TEMP TABLE verdict AS
SELECT r.ord, r.code, r.descr,
       s.tps, s.p95, s.p99,
       (SELECT sum(failed + aborted) FROM j WHERE j.code = r.code)   AS errors,
       r.deadlocks_after - r.deadlocks_before                       AS deadlocks,
       lk.wait_s, lk.longest_ms, r.stock_ok,
       round(1000 * coalesce(lk.wait_s, 0) / nullif((SELECT sum(ok) FROM j WHERE j.code = r.code), 0), 2) AS wait_per_tx_ms,
       (s.p95 < 100 AND r.deadlocks_after = r.deadlocks_before
        AND (SELECT sum(failed + aborted) FROM j WHERE j.code = r.code) = 0
        AND r.stock_ok)                                               AS passed
FROM bench_conc.runs r
JOIN j s ON s.code = r.code AND s.job = 'sale'
LEFT JOIN lk ON lk.code = r.code;

-- ---------------------------------------------------------------------
SELECT format(E'# Kassir ERP — конкурентный тест (шаг 4б)\n\n'
  'Дата прогона: %s (Бишкек)  \n'
  'Сервер: %s  \n'
  'Ядер в контейнере: %s · max_connections = %s · shared_buffers = %s · synchronous_commit = %s · fsync = %s · deadlock_timeout = %s\n\n'
  '**Цель ТЗ: p95 чека < 100 мс, взаимоблокировок — ноль.** '
  'Итог: пройдено **%s из %s** сценариев.\n',
  to_char(now() AT TIME ZONE 'Asia/Bishkek', 'YYYY-MM-DD HH24:MI'),
  version(), :'nproc',
  current_setting('max_connections'), current_setting('shared_buffers'),
  current_setting('synchronous_commit'), current_setting('fsync'),
  current_setting('deadlock_timeout'),
  (SELECT count(*) FILTER (WHERE passed) FROM verdict), (SELECT count(*) FROM verdict));

SELECT E'## Итог\n\n'
  '| # | Сценарий | Чеков/с | p95 чека | p99 чека | Ошибок | Взаимоблокировок | Ожидание блокировок, Σ с | Ожидание на транзакцию | stock = Σ движений | Итог |\n'
  '|--:|---|--:|--:|--:|--:|--:|--:|--:|:-:|---|\n' ||
  string_agg(format('| %s | %s | %s | %s мс | %s мс | %s | %s | %s | %s мс | %s | %s |',
    code, descr, tps, p95, p99, errors, deadlocks, coalesce(wait_s, 0), coalesce(wait_per_tx_ms, 0),
    CASE WHEN stock_ok THEN 'да' ELSE '**НЕТ**' END,
    CASE WHEN passed THEN '✅ пройдено' ELSE '❌ НЕ пройдено' END), E'\n' ORDER BY ord) || E'\n'
FROM verdict;

SELECT format(E'## Как мерили\n\n'
  '- Каждый сценарий — %s с нагрузки `pgbench` (`-T`), без разгона; клиенты подключаются ролью `kassir_app`, '
  '`app.tenant_id` задаётся в каждой транзакции — RLS включён, как в приложении.\n'
  '- **Нагрузка на пределе** (все сценарии, кроме S2r): каждая касса шлёт следующий чек сразу после ответа, без пауз. '
  '«Чеков/с» — это пропускная способность сервера, а не реальная нагрузка: живая касса пробивает чек раз в минуту, '
  'филиал с 6 кассами — не больше 1 чека в секунду в пик.\n'
  '- **S2r** — фиксированный темп `pgbench -R 100`: 100 чеков/с на все 50 касс вместе. '
  'Задержка тогда считается от запланированного момента старта, то есть включает очередь на клиенте.\n'
  '- **Чек** ([tx_sale.sql](concurrency/tx_sale.sql)) = одна транзакция: скан двух штрихкодов с остатком, '
  '`INSERT` чека, оплата, две строки одним `INSERT` (триггеры → движения → `stock`), `COMMIT`. '
  'Строки — последним оператором: строка `stock` заблокирована только до `COMMIT`. '
  'S2a — тот же чек со строками до оплаты, для сравнения. '
  'Все 50 касс — в одном филиале, у каждой своя смена: это худший случай для строк `stock`.\n'
  '- **Ходовая пара** (S2, S2a, S2r): первая строка каждого чека — одна и та же пара, вторая — случайная, '
  'то есть каждый чек блокирует две строки `stock` в разном порядке сканирования. '
  'Триггер обновляет их в порядке `(tenant, store, variant)` — проверяется, что взаимоблокировок нет.\n'
  '- **Отчёт** ([tx_report.sql](concurrency/tx_report.sql)) — выручка/себестоимость/прибыль по дням за год, все филиалы (запрос № 9 из bench.sql).\n'
  '- **Телефон** ([tx_phone.sql](concurrency/tx_phone.sql)) — модель + 10 пар со штрихкодами + 3 фото (`storage_key`) + '
  'накладная на 10 строк и её приёмка в том же филиале, где идут продажи.\n'
  '- Задержка — время транзакции на стороне `pgbench` (лог `-l`), включая `COMMIT` с `fsync`. '
  '`pgbench` работает в том же контейнере, что и Postgres, и делит с ним %s ядер — нагрузочный клиент сам отнимает CPU.\n'
  '- **Ожидания на блокировках**: раз в ~20 мс снимок `pg_locks` (`granted = false`); суммарное время ≈ Σ (ждущих × интервал). '
  '**Взаимоблокировки** — разница счётчика `pg_stat_database.deadlocks` до и после сценария плюс неуспешные транзакции в логе `pgbench`.\n'
  '- После каждого сценария — сверка `stock = SUM(stock_movements)` по всей базе.\n',
  (SELECT max(seconds) FROM bench_conc.runs), :'nproc');

SELECT E'## Потоки нагрузки\n\n'
  '| # | Поток | Клиентов | Успешно | Ошибок | Транзакций/с | Среднее | p50 | p95 | p99 | Макс |\n'
  '|--:|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n' ||
  string_agg(format('| %s | %s | %s | %s | %s | %s | %s мс | %s мс | %s мс | %s мс | %s мс |',
    code,
    CASE job WHEN 'sale' THEN 'кассы (чек)' WHEN 'report' THEN 'отчёты' WHEN 'phone' THEN 'телефоны (товар)' ELSE job END,
    clients, ok,
    CASE WHEN failed + aborted = 0 THEN '0'
         ELSE format('**%s** (deadlock %s, serialization %s, клиентов прервано %s)', failed + aborted, failed_deadlock, failed_serial, aborted) END,
    tps, avg_ms, p50, p95, p99, max_ms), E'\n' ORDER BY ord, job DESC) || E'\n'
FROM j;

SELECT E'## Блокировки\n\n'
  '| # | Снимков | Доля снимков с ожиданием | Ждущих в среднем | Ждущих максимум | Σ ожидания, с | из них строка (tuple) | чужая транзакция (transactionid) | прочее | Самое долгое единичное ожидание |\n'
  '|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n' ||
  string_agg(format('| %s | %s | %s %% | %s | %s | %s | %s | %s | %s | %s мс |',
    lk.code, samples, pct_with_wait, avg_waiting, max_waiting, wait_s, wait_tuple_s, wait_txid_s, wait_other_s, longest_ms),
    E'\n' ORDER BY r.ord) || E'\n\n'
  'Σ ожидания — сколько процессо-секунд кассы и телефоны простояли в очереди за блокировкой за весь сценарий. '
  'Ожидание `transactionid` — ждём, пока другая транзакция закоммитит строку `stock`, которую мы хотим обновить.\n'
FROM lk JOIN bench_conc.runs r USING (code);

SELECT format(E'## Объём после теста\n\n'
  'Чеков с bench-касс: %s · товаров с телефонов: %s · движений в журнале: %s · строк `stock`: %s · '
  'остаток ходовой пары в филиале теста: %s (отрицательный остаток разрешён схемой).\n',
  (SELECT count(*) FROM sales WHERE device_id IN (SELECT id FROM devices WHERE name LIKE 'bench-kassa-%')),
  (SELECT count(*) FROM products WHERE article LIKE 'PH-%'),
  (SELECT count(*) FROM stock_movements),
  (SELECT count(*) FROM stock),
  (SELECT s.qty FROM stock s JOIN product_variants v ON v.id = s.variant_id
    WHERE v.barcode = '46' || lpad((SELECT v FROM bench_conc.params WHERE k = 'hotn'), 11, '0')
      AND s.store_id = (SELECT v::uuid FROM bench_conc.params WHERE k = 'store')));

SELECT E'## Вывод pgbench\n\n' ||
  string_agg(format(E'<details><summary>%s · %s</summary>\n\n```\n%s```\n</details>\n', code, job, output),
             E'\n' ORDER BY code, job DESC)
FROM bench_conc.jobs;

\o
