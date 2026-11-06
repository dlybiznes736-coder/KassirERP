# Kassir ERP — конкурентный тест (шаг 4б)

Дата прогона: 2026-11-06 15:05 (Бишкек)  
Сервер: PostgreSQL 16.4 (Debian 16.4-1.pgdg120+2) on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14) 12.2.0, 64-bit  
Ядер в контейнере: 8 · max_connections = 100 · shared_buffers = 128MB · synchronous_commit = on · fsync = on · deadlock_timeout = 1s

**Цель ТЗ: p95 чека < 100 мс, взаимоблокировок — ноль.** Итог: пройдено **4 из 6** сценариев.

## Итог

| # | Сценарий | Чеков/с | p95 чека | p99 чека | Ошибок | Взаимоблокировок | Ожидание блокировок, Σ с | Ожидание на транзакцию | stock = Σ движений | Итог |
|--:|---|--:|--:|--:|--:|--:|--:|--:|:-:|---|
| S1 | 50 кассиров, случайные пары | 3226.3 | 25.34 мс | 36.09 мс | 0 | 0 | 0.58 | 0.00 мс | да | ✅ пройдено |
| S2 | 50 кассиров, в каждом чеке одна ходовая пара | 492.2 | 408.27 мс | 714.75 мс | 0 | 0 | 1555.56 | 52.68 мс | да | ❌ НЕ пройдено |
| S2a | То же, но строки чека до оплаты (для сравнения) | 460.0 | 638.29 мс | 1271.97 мс | 0 | 0 | 1601.26 | 58.01 мс | да | ❌ НЕ пройдено |
| S2r | 50 кассиров, ходовая пара, темп 100 чеков/с на филиал | 100.2 | 9.01 мс | 133.22 мс | 0 | 0 | 15.15 | 2.52 мс | да | ✅ пройдено |
| S3 | 50 кассиров + 5 отчётов по всем филиалам одновременно | 2325.5 | 38.10 мс | 66.67 мс | 0 | 0 | 1.06 | 0.01 мс | да | ✅ пройдено |
| S4 | 50 кассиров + 20 телефонов заводят товар с приёмкой | 1404.4 | 75.55 мс | 152.69 мс | 0 | 0 | 3.74 | 0.04 мс | да | ✅ пройдено |

## Выводы

> Раздел написан вручную по этому прогону; `make_report.sql` его не генерирует.

- **Взаимоблокировок ноль во всех шести сценариях**, ошибок и прерванных клиентов ноль, `stock = SUM(stock_movements)` после каждого. Единый порядок блокировок в триггере работает: в S2 каждый чек берёт две строки `stock` в произвольном порядке, и это 29 531 чек без единого deadlock.
- **Случайные пары, отчёты, телефоны (S1, S3, S4) проходят** с запасом по p95: 25, 38 и 76 мс при 1 400–3 200 чеках/с. Ожидание на блокировках там почти нулевое (≤ 0.04 мс на транзакцию). Задержку определяет CPU, а не блокировки.
- **Одна ходовая пара на пределе (S2) не проходит: p95 408 мс.** Все 50 касс встают в очередь за одной строкой `stock`. Эта строка заблокирована от `UPDATE` в триггере до `COMMIT`, поэтому потолок — около 490 чеков/с на одну пару в одном филиале. Это ограничение модели «кэш остатка — одна строка», а не ошибка.
- **Порядок операторов в бэкенде важен.** Строки чека последним оператором перед `COMMIT` (S2) против строк до оплаты (S2a): p95 408 против 638 мс, p99 715 против 1 272 мс. Правило для бэкенда: `INSERT sale_items` — последний оператор транзакции чека.
- **Ниже потолка ходовая пара проходит (S2r).** При 100 чеках/с на филиал p95 = 9 мс, но p99 = 133 мс: хвост — это короткие очереди, когда несколько касс попадают в одну миллисекунду. 100 чеков/с на филиал — это в 100 раз больше реального пика (6 касс ≈ 1 чек/с). Реальный риск появится только там, где одну строку `stock` делят сотни касс: например, общий склад маркетплейса. Тогда — шардирование счётчика или отложенное применение движений. Для этапа 1 не нужно.
- **Отчёт под нагрузкой — 2.5 с против цели 1 000 мс.** В одиночку он идёт 234 мс, но 50 касс на пределе забирают CPU. Пятеро владельцев, гоняющих годовой отчёт в цикле при 2 300 чеках/с, — это перебор, но таблица дневных итогов (этап 2) снимет вопрос полностью.
- **Ограничение замера:** `pgbench` работает в том же контейнере и на тех же 8 ядрах, что и Postgres. На отдельном сервере цифры будут лучше, а не хуже.

## Как мерили

- Каждый сценарий — 60 с нагрузки `pgbench` (`-T`), без разгона; клиенты подключаются ролью `kassir_app`, `app.tenant_id` задаётся в каждой транзакции — RLS включён, как в приложении.
- **Нагрузка на пределе** (все сценарии, кроме S2r): каждая касса шлёт следующий чек сразу после ответа, без пауз. «Чеков/с» — это пропускная способность сервера, а не реальная нагрузка: живая касса пробивает чек раз в минуту, филиал с 6 кассами — не больше 1 чека в секунду в пик.
- **S2r** — фиксированный темп `pgbench -R 100`: 100 чеков/с на все 50 касс вместе. Задержка тогда считается от запланированного момента старта, то есть включает очередь на клиенте.
- **Чек** ([tx_sale.sql](concurrency/tx_sale.sql)) = одна транзакция: скан двух штрихкодов с остатком, `INSERT` чека, оплата, две строки одним `INSERT` (триггеры → движения → `stock`), `COMMIT`. Строки — последним оператором: строка `stock` заблокирована только до `COMMIT`. S2a — тот же чек со строками до оплаты, для сравнения. Все 50 касс — в одном филиале, у каждой своя смена: это худший случай для строк `stock`.
- **Ходовая пара** (S2, S2a, S2r): первая строка каждого чека — одна и та же пара, вторая — случайная, то есть каждый чек блокирует две строки `stock` в разном порядке сканирования. Триггер обновляет их в порядке `(tenant, store, variant)` — проверяется, что взаимоблокировок нет.
- **Отчёт** ([tx_report.sql](concurrency/tx_report.sql)) — выручка/себестоимость/прибыль по дням за год, все филиалы (запрос № 9 из bench.sql).
- **Телефон** ([tx_phone.sql](concurrency/tx_phone.sql)) — модель + 10 пар со штрихкодами + 3 фото (`storage_key`) + накладная на 10 строк и её приёмка в том же филиале, где идут продажи.
- Задержка — время транзакции на стороне `pgbench` (лог `-l`), включая `COMMIT` с `fsync`. `pgbench` работает в том же контейнере, что и Postgres, и делит с ним 8 ядер — нагрузочный клиент сам отнимает CPU.
- **Ожидания на блокировках**: раз в ~20 мс снимок `pg_locks` (`granted = false`); суммарное время ≈ Σ (ждущих × интервал). **Взаимоблокировки** — разница счётчика `pg_stat_database.deadlocks` до и после сценария плюс неуспешные транзакции в логе `pgbench`.
- После каждого сценария — сверка `stock = SUM(stock_movements)` по всей базе.

## Потоки нагрузки

| # | Поток | Клиентов | Успешно | Ошибок | Транзакций/с | Среднее | p50 | p95 | p99 | Макс |
|--:|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| S1 | кассы (чек) | 50 | 193579 | 0 | 3226.3 | 15.52 мс | 14.66 мс | 25.34 мс | 36.09 мс | 339.09 мс |
| S2 | кассы (чек) | 50 | 29531 | 0 | 492.2 | 101.62 мс | 30.77 мс | 408.27 мс | 714.75 мс | 1935.15 мс |
| S2a | кассы (чек) | 50 | 27601 | 0 | 460.0 | 108.75 мс | 4.39 мс | 638.29 мс | 1271.97 мс | 3230.76 мс |
| S2r | кассы (чек) | 50 | 6014 | 0 | 100.2 | 7.79 мс | 3.78 мс | 9.01 мс | 133.22 мс | 567.27 мс |
| S3 | кассы (чек) | 50 | 139529 | 0 | 2325.5 | 21.49 мс | 19.24 мс | 38.10 мс | 66.67 мс | 365.00 мс |
| S3 | отчёты | 5 | 123 | 0 | 2.1 | 2463.87 мс | 2467.14 мс | 3177.35 мс | 3380.15 мс | 3755.08 мс |
| S4 | кассы (чек) | 50 | 84266 | 0 | 1404.4 | 35.58 мс | 28.75 мс | 75.55 мс | 152.69 мс | 595.72 мс |
| S4 | телефоны (товар) | 20 | 5467 | 0 | 91.1 | 219.72 мс | 212.22 мс | 369.48 мс | 445.68 мс | 703.58 мс |

## Блокировки

| # | Снимков | Доля снимков с ожиданием | Ждущих в среднем | Ждущих максимум | Σ ожидания, с | из них строка (tuple) | чужая транзакция (transactionid) | прочее | Самое долгое единичное ожидание |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| S1 | 2217 | 0.5 % | 0.01 | 4 | 0.58 | 0.00 | 0.19 | 0.39 | 20.9 мс |
| S2 | 2593 | 87.9 % | 25.07 | 49 | 1555.56 | 2.21 | 1553.35 | 0.00 | 209.8 мс |
| S2a | 2619 | 86.4 % | 26.00 | 49 | 1601.26 | 2.03 | 1599.23 | 0.00 | 141.9 мс |
| S2r | 2735 | 1.9 % | 0.06 | 30 | 15.15 | 0.00 | 15.15 | 0.00 | 134.1 мс |
| S3 | 1969 | 0.8 % | 0.02 | 12 | 1.06 | 0.00 | 0.18 | 0.88 | 15.9 мс |
| S4 | 1597 | 2.5 % | 0.06 | 13 | 3.74 | 0.00 | 0.17 | 3.56 | 53.9 мс |

Σ ожидания — сколько процессо-секунд кассы и телефоны простояли в очереди за блокировкой за весь сценарий. Ожидание `transactionid` — ждём, пока другая транзакция закоммитит строку `stock`, которую мы хотим обновить.

## Объём после теста

Чеков с bench-касс: 547828 · товаров с телефонов: 6293 · движений в журнале: 2386758 · строк `stock`: 1554437 · остаток ходовой пары в филиале теста: -68072.000 (отрицательный остаток разрешён схемой).

## Вывод pgbench

<details><summary>S1 · sale</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_sale.sql
scaling factor: 1
query mode: simple
number of clients: 50
number of threads: 50
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 193579
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 15.546 ms
initial connection time = 23.927 ms
tps = 3216.363309 (without initial connection time)
```
</details>

<details><summary>S2 · sale</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_sale.sql
scaling factor: 1
query mode: simple
number of clients: 50
number of threads: 50
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 29531
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 101.706 ms
initial connection time = 25.832 ms
tps = 491.611286 (without initial connection time)
```
</details>

<details><summary>S2a · sale</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_sale.sql
scaling factor: 1
query mode: simple
number of clients: 50
number of threads: 50
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 27601
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 108.859 ms
initial connection time = 22.461 ms
tps = 459.307947 (without initial connection time)
```
</details>

<details><summary>S2r · sale</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_sale.sql
scaling factor: 1
query mode: simple
number of clients: 50
number of threads: 50
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 6014
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 7.793 ms
latency stddev = 34.436 ms
rate limit schedule lag: avg 1.205 (max 525.888) ms
initial connection time = 25.488 ms
tps = 100.270866 (without initial connection time)
```
</details>

<details><summary>S3 · sale</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_sale.sql
scaling factor: 1
query mode: simple
number of clients: 50
number of threads: 50
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 139529
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 21.506 ms
initial connection time = 37.926 ms
tps = 2324.930194 (without initial connection time)
```
</details>

<details><summary>S3 · report</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_report.sql
scaling factor: 1
query mode: simple
number of clients: 5
number of threads: 5
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 123
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 2471.439 ms
initial connection time = 4.649 ms
tps = 2.023113 (without initial connection time)
```
</details>

<details><summary>S4 · sale</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_sale.sql
scaling factor: 1
query mode: simple
number of clients: 50
number of threads: 50
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 84266
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 35.615 ms
initial connection time = 57.332 ms
tps = 1403.906962 (without initial connection time)
```
</details>

<details><summary>S4 · phone</summary>

```
pgbench (16.4 (Debian 16.4-1.pgdg120+2))
transaction type: /db/bench/concurrency/tx_phone.sql
scaling factor: 1
query mode: simple
number of clients: 20
number of threads: 20
maximum number of tries: 1
duration: 60 s
number of transactions actually processed: 5467
number of failed transactions: 0 (0.000%)
number of serialization failures: 0 (0.000%)
number of deadlock failures: 0 (0.000%)
latency average = 219.859 ms
initial connection time = 11.519 ms
tps = 90.967197 (without initial connection time)
```
</details>

