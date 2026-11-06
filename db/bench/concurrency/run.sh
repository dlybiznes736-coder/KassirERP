#!/bin/bash
# =====================================================================
# Kassir ERP — конкурентный тест (шаг 4б). Запускать после generate.sql:
#
#   docker compose exec -T postgres bash /db/bench/concurrency/run.sh
#   docker compose cp postgres:/tmp/kassir_concurrency_report.md db/bench/CONCURRENCY_REPORT.md
#
# Переменные: DB (kassir), T — секунд на сценарий (60).
# Шесть сценариев, в каждом pgbench от роли kassir_app (RLS включён):
#   S1   50 кассиров, случайные пары
#   S2   50 кассиров, в каждом чеке ходовая пара
#   S2a  то же, строки чека до оплаты — сравнение порядка операторов
#   S2r  то же, что S2, но фиксированный темп 100 чеков/с (pgbench -R)
#   S3   50 кассиров + 5 отчётов «все филиалы за год» одновременно
#   S4   50 кассиров + 20 телефонов заводят товар с приёмкой
# Во время сценария раз в ~20 мс снимаются ожидания на блокировках
# (pg_locks), до и после — счётчик взаимоблокировок pg_stat_database.
# Повторный запуск безопасен: результаты пересоздаются, данные копятся.
# =====================================================================
set -euo pipefail

DB=${DB:-kassir}
T=${T:-60}
DIR=/db/bench/concurrency
OUT=/tmp/kassir_conc
REPORT=/tmp/kassir_concurrency_report.md

rm -rf "$OUT" && mkdir -p "$OUT" && chmod 777 "$OUT"
PSQL=(psql -U postgres -d "$DB" -X -q -v ON_ERROR_STOP=1)

echo "== Подготовка"
"${PSQL[@]}" -f "$DIR/setup.sql"
PARAMS=$("${PSQL[@]}" -tA -c "SELECT string_agg('-D ' || k || '=' || v, ' ' ORDER BY k) FROM bench_conc.params")
"${PSQL[@]}" -c "CHECKPOINT"

# run_scenario КОД ПОРЯДОК "Описание" "поток клиентов скрипт [-D ...]" ...
run_scenario() {
  local code=$1 ord=$2 descr=$3
  shift 3
  echo "== $code: $descr ($T с)"
  "${PSQL[@]}" -c "CALL bench_conc.begin_run('$code', $ord, '$descr', $T)"

  "${PSQL[@]}" -c "CALL bench_conc.sample_locks('$code', $T + 1)" &
  local sampler=$!

  local pids=() specs=()
  for spec in "$@"; do
    local job clients script extra
    read -r job clients script extra <<<"$spec"
    # Имя базы — последним аргументом: у pgbench «-d» значит --debug.
    # shellcheck disable=SC2086
    pgbench -n -U kassir_app -c "$clients" -j "$clients" -T "$T" \
      -f "$DIR/$script" $PARAMS $extra \
      --failures-detailed -l --log-prefix="$OUT/log_${code}_${job}" \
      "$DB" > "$OUT/${code}_${job}.out" 2>&1 &
    pids+=($!)
    specs+=("$job $clients $script")
  done

  for p in "${pids[@]}"; do wait "$p" || true; done
  wait "$sampler"

  for s in "${specs[@]}"; do
    local job clients script
    read -r job clients script <<<"$s"
    cat "$OUT/log_${code}_${job}".* > "$OUT/${code}_${job}.log" 2>/dev/null || : > "$OUT/${code}_${job}.log"
    chmod 644 "$OUT/${code}_${job}.log" "$OUT/${code}_${job}.out"
    "${PSQL[@]}" -c "CALL bench_conc.load_job('$code', '$job', $clients, '$script', '$OUT/${code}_${job}.log', '$OUT/${code}_${job}.out')"
    grep -E "^(tps|latency average|number of failed|number of transactions actually)" "$OUT/${code}_${job}.out" | sed "s/^/   [$job] /" || true
  done

  "${PSQL[@]}" -c "CALL bench_conc.end_run('$code')"
}

# Без -R pgbench даёт нагрузку на пределе: каждая касса шлёт следующий чек
# сразу после ответа. С -R — фиксированный темп на все кассы вместе.
run_scenario S1 1 "50 кассиров, случайные пары" \
  "sale 50 tx_sale.sql -D hot=0 -D items_last=1"

run_scenario S2 2 "50 кассиров, в каждом чеке одна ходовая пара" \
  "sale 50 tx_sale.sql -D hot=1 -D items_last=1"

run_scenario S2a 3 "То же, но строки чека до оплаты (для сравнения)" \
  "sale 50 tx_sale.sql -D hot=1 -D items_last=0"

run_scenario S2r 4 "50 кассиров, ходовая пара, темп 100 чеков/с на филиал" \
  "sale 50 tx_sale.sql -D hot=1 -D items_last=1 -R 100"

run_scenario S3 5 "50 кассиров + 5 отчётов по всем филиалам одновременно" \
  "sale 50 tx_sale.sql -D hot=0 -D items_last=1" \
  "report 5 tx_report.sql"

run_scenario S4 6 "50 кассиров + 20 телефонов заводят товар с приёмкой" \
  "sale 50 tx_sale.sql -D hot=0 -D items_last=1" \
  "phone 20 tx_phone.sql"

echo "== Отчёт"
"${PSQL[@]}" -v nproc="$(nproc)" -v report="$REPORT" -f "$DIR/make_report.sql"
echo "Готово: $REPORT"
echo "Забрать: docker compose cp postgres:$REPORT db/bench/CONCURRENCY_REPORT.md"
