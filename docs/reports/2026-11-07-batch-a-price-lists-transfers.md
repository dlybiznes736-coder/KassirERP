# Партия A: прайс-листы, расхождение цены, перемещение между филиалами

Дата: 2026-11-07 · Миграции `0008_price_lists.sql`, `0009_transfers.sql` · Обе только добавляют, закрытую схему не меняют.

**Итог: всё из партии сделано. 90 тестов проходят — 80 прежних, все без изменений, и 10 новых. Скан с ценой листа медленнее на 0.1–0.2 мс.**

## Прайс-листы

- **Листы и цены в них:** `price_lists` и `price_list_items` (лист, пара, цена). У филиала — `stores.price_list_id`.
- **Цена пары в филиале** — цена его листа, если она есть, иначе базовая (`product_variants.sale_price`). У филиала без листа — базовая.
- **Скан, поиск и карточка товара** возвращают цену для филиала из токена. В скане и карточке есть ещё `basePrice` и `priceListId`; `priceListId` = null значит базовую цену.
- **Журнал `price_changes`** — кто, когда, было, стало:
  - пишет только база, триггерами уровня оператора; у роли приложения — только чтение, записать в журнал своё она не может;
  - загрузка 500 000 цен — одна вставка в журнал, 17 с вместе с самими ценами;
  - журналируется и смена базовой цены пары (`price_list_id` = null);
  - «кто» — из `app.user_id`, который ставит `TenantDb` из проверенного токена, тем же оператором, что `app.tenant_id`; архитектурный тест «одна точка» по-прежнему проходит.
- **API:**
  - `POST /price-lists`;
  - `GET /price-lists`;
  - `GET|PUT /price-lists/{id}/items`;
  - `DELETE /price-lists/{id}/items/{variantId}`;
  - `PUT /stores/{id}/price-list`;
  - `GET /price-changes?priceListId=&variantId=`.
- **Кто меняет цены:** владелец, администратор, менеджер. Кладовщик заводит товары, но не цены — 403.
- **В базе:** лист, цена в листе и лист филиала — только своей фирмы (триггеры).

## Расхождение цены в чеке

- У строки чека появились `listPrice` (цена листа филиала или базовая на момент приёма чека) и `priceMismatch`.
- Касса пробила другую цену — строка помечена, **чек принят**.
- У строк возврата `listPrice` = null: их цена берётся из исходного чека.

## Перемещение между филиалами

- **Документ:** черновик → `in_transit` (отправлен) → `received` (принят).
  - Отправляет филиал из токена: остаток отправителя уменьшается, движение `transfer_out`.
  - Принимает филиал-получатель: его остаток растёт на **принятое** количество, движение `transfer_in`.
  - Пока документ в пути, товара нет ни в одном из двух остатков. Увидеть его можно в `GET /transfers/incoming` у получателя.
- **Принято меньше отправленного:**
  - в строке остаются `qtySent`, `qtyReceived` и `shortage`, у документа — `hasDiscrepancy`;
  - остатки обоих филиалов отражают то, что есть физически, а недостача видна по документу;
  - принять больше, чем отправили, нельзя — 400.
- **Повторы:**
  - создание, отправка и приём с теми же данными — 200, второго документа или движения нет;
  - повтор приёма с другими количествами — 409.
- **В базе:**
  - статусы только вперёд;
  - строки после отправки не меняются, кроме однократной отметки «принято»;
  - принятый документ не меняется и не удаляется;
  - перемещение — только между филиалами своей фирмы, пары — своей фирмы;
  - движения пишет триггер; уникальность (причина, строка документа) не даёт задвоить их даже при сбое.

## Тесты (10 новых)

| Требование | Тест | Результат |
|---|---|---|
| Два филиала с разными листами — разные цены; без листа — базовая | `PriceListTests.Stores_with_different_lists_get_different_prices_and_store_without_list_gets_base` | филиал 1 (без листа) — 4 990.00; филиал 2 — 5 100.00; филиал 3 — 4 800.00; пары, которой нет в листе, — базовая |
| | `Search_and_product_card_show_the_store_price` | поиск и карточка — цена листа, `basePrice` — 4 990.00 |
| Журнал цен | `Price_changes_are_journaled_who_when_old_new` | нет → 5 100; 5 100 → 5 200; та же цена — записи нет; 5 200 → удалена; «кто» — менеджер |
| | `Only_managers_change_prices_and_lists_stay_inside_the_tenant` | кладовщик — 403; лист фирмы B филиалу A — 400; цены листа B — 404; пара фирмы B в листе — 400 |
| Чек с заниженной ценой проходит, строка помечена | `SaleTests.Sale_below_the_list_price_is_accepted_and_the_line_is_flagged` | 201; кроссовки по 4 990 при листе 5 200 — `priceMismatch` = true; колбаса по 520 = базовая — false |
| Отправка уменьшает, приём увеличивает | `TransferTests.Sending_decreases_sender_stock_and_receiving_increases_receiver_stock` | черновик 10 / 0 → отправка 6 / 0 (в пути) → приём 6 / 4; остатки сходятся с журналом |
| Повторная отправка не создаёт дубль | `Resending_does_not_create_duplicates` | создание ×2, отправка ×2, приём ×2 — 201 и 200; в базе 1 документ, 1 строка, 1 списание, 1 приход |
| Принято меньше — расхождение | `Receiving_less_than_sent_records_the_shortage` | отправлено 10, принято 7: `shortage` = 3, `hasDiscrepancy` = true, остатки 0 / 7, сходятся с журналом |
| | `Wrong_store_wrong_stage_and_over_receipt_are_refused` | принять черновик — 409; отправить из филиала-получателя — 403; принять в филиале-отправителе — 403; принять больше — 400 |
| | `Transfers_stay_inside_the_tenant_and_received_ones_do_not_change` | филиал или пара фирмы B — 400; правка или удаление принятого ролью приложения напрямую — отказ |

**Проверка, что тесты ловят то, что должны** (временные мутации, код возвращён):

| Мутация | Упал тест |
|---|---|
| Скан берёт базовую цену вместо цены листа | `Stores_with_different_lists_…` |
| Расхождение цены никогда не помечается | `Sale_below_the_list_price_…` |

## Замер скана до и после

`tools/KassirERP.ApiBench`, Release, база 1×, по два прогона. «До» — код на `2aac2fd`. «С листом» — «Филиалу 1» назначен лист на все 500 000 пар (`db/bench/scan_price_list.sql`): у каждой пары есть цена в листе, это худший случай.

| | p50 до | p50 после, без листа | p50 после, с листом | p95 до | p95 после, с листом |
|---|--:|--:|--:|--:|--:|
| Скан, одна пара | 1.14 / 1.15 мс | 1.23 / 1.24 мс | 1.25 / 1.27 мс | 1.80 / 1.88 мс | 1.81 / 1.83 мс |
| Скан, случайные пары | 0.92 / 0.93 мс | 1.05 / 1.05 мс | 1.08 / 1.11 мс | 1.38 / 1.62 мс | 1.63 / 1.60 мс |
| В самой базе (`EXPLAIN`, случайные) | 0.25 мс | 0.31 мс | 0.33–0.36 мс | | |

Цена листа добавляет два поиска по первичному ключу (филиал, цена в листе): 0.1–0.2 мс на скан.

Замер чеков теперь работает на **одной стендовой кассе** (`b0000000-…-be05`, создаёт генератор). Касса узнаёт своё состояние через `GET /devices/me`: закрывает смену, если та осталась открытой, и продолжает нумерацию чеков — в двух запусках подряд чеки 1–1 250 и 1 251–2 500. Новых касс замер больше не регистрирует. Время чека — p50 7.0–10.8 мс, p95 8.5–13.2 мс, на уровне прошлой партии.

`GET /devices/me` — штатный эндпоинт: настоящей кассе он нужен после переустановки, чтобы продолжить нумерацию и не открыть вторую смену.

## Команды, чтобы прогнать самому (PowerShell)

Из `C:\Users\user\Desktop\KassirERP\KassirERP`, Docker запущен.

```powershell
# 1. Тесты — ожидается: пройдено 90
dotnet test tests/KassirERP.Api.Tests

# 2. Проверки базы — 11 строк OK и «Все проверки пройдены»
docker compose exec -T postgres psql -U postgres -d kassir -X -f /db/tests/smoke.sql

# 3. Миграции 0008–0009 в твоей базе уже применены — ожидается schema_version = 9
docker compose exec -T postgres psql -U postgres -d kassir -X -c "SELECT max(schema_version) FROM tenants"

# 4. Генератор на свежей базе со всеми миграциями (около 1.5 мин): 9 миграций, OK, «Все проверки пройдены»
docker compose exec -T postgres bash /db/create_database.sh kassir_check --recreate
docker compose exec -T postgres psql -U postgres -d kassir_check -X -v ON_ERROR_STOP=1 -f /db/bench/generate.sql
docker compose exec -T postgres psql -U postgres -d kassir_check -X -f /db/tests/smoke.sql
docker compose exec -T postgres psql -U postgres -d postgres -c "DROP DATABASE kassir_check"
```

**5. Ручной сценарий.** Запусти API (`dotnet run --project src/KassirERP.Api --launch-profile http`) и выполни по порядку запросы из [KassirERP.Api.http](../../src/KassirERP.Api/KassirERP.Api.http). Новые шаги 18–23:
- создать прайс-лист, цена колбасы 540, назначить его «Филиалу 1»;
- скан весового — 1.250 × 540 = 675.00, базовая 520;
- журнал цен;
- снять лист.

Все шаги сценария я прогнал на живом API, ответы — ожидаемые.

**6. Замер скана: без листа, затем с листом на 500 000 пар.**

```powershell
dotnet build KassirERP.slnx -c Release
$env:ASPNETCORE_ENVIRONMENT = 'Production'; $env:ASPNETCORE_URLS = 'http://localhost:5260'
$env:ConnectionStrings__Kassir = 'Host=localhost;Port=15432;Database=kassir;Username=kassir_app;Password=kassir_app_dev'
$env:Jwt__SigningKey = 'bench-only-signing-key-0123456789abcdef-012345'
$api = Start-Process dotnet -ArgumentList 'KassirERP.Api.dll' -WorkingDirectory 'src/KassirERP.Api/bin/Release/net10.0' -PassThru -WindowStyle Hidden
Start-Sleep 5
dotnet tools/KassirERP.ApiBench/bin/Release/net10.0/KassirERP.ApiBench.dll --out scan_nolist.md
docker compose exec -T postgres psql -U postgres -d kassir -X -v ON_ERROR_STOP=1 -f /db/bench/scan_price_list.sql
dotnet tools/KassirERP.ApiBench/bin/Release/net10.0/KassirERP.ApiBench.dll --out scan_list.md
docker compose exec -T postgres psql -U postgres -d kassir -X -c "UPDATE stores SET price_list_id = NULL WHERE price_list_id = 'b0000000-0000-0000-0000-00000000be10'"
dotnet tools/KassirERP.ApiBench/bin/Release/net10.0/KassirERP.ApiBench.dll --mode sales --sales 1000 --returns 200 --out sales.md
Stop-Process $api
Get-Content scan_nolist.md, scan_list.md, sales.md -Encoding utf8 | Select-String '^\| (Скан|Чек|Возврат)'
```

Ожидается:
- скан p50 около 1.0–1.3 мс в обоих вариантах;
- чек p50 около 7–11 мс;
- при повторном запуске замер чеков продолжает нумерацию на той же стендовой кассе.

## Ограничения

- **Расхождение сверяется с текущей ценой листа на момент приёма чека.** Если цена поменялась после продажи, но до синхронизации кассы, строка будет помечена, хотя касса была права. Восстановить цену на момент чека можно по журналу `price_changes` — это для отчёта о расхождениях, когда до него дойдёт.
- **Товар в пути не виден в остатках ни одного филиала**, только в документах (`/transfers/incoming`). Отдельного отчёта «в пути» нет.
- **Отменить или вернуть отправленное перемещение нельзя:** это делается встречным перемещением.
