#!/bin/bash
# =====================================================================
# Создаёт базу с нуля: схема v1 (миграция 0001) + db/migrations/*.sql по порядку.
#
#   docker compose exec -T postgres bash /db/create_database.sh kassir_x3
#   docker compose exec -T postgres bash /db/create_database.sh kassir --recreate
#   docker compose exec -T postgres bash /db/create_database.sh kassir --recreate --until 0002
#
# --recreate  удалить базу, если есть (все данные в ней пропадут)
# --until N   применить миграции только до номера N включительно
# =====================================================================
set -euo pipefail

DB=${1:?Укажите имя базы}
shift
RECREATE=0
UNTIL=9999
while [ $# -gt 0 ]; do
  case "$1" in
    --recreate) RECREATE=1 ;;
    --until) UNTIL=$((10#$2)); shift ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
  esac
  shift
done

PSQL=(psql -U postgres -X -q -v ON_ERROR_STOP=1)

exists=$("${PSQL[@]}" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DB'")
if [ "$exists" = "1" ]; then
  if [ "$RECREATE" = "1" ]; then
    echo "== Удаляю базу $DB"
    "${PSQL[@]}" -d postgres -c "DROP DATABASE \"$DB\" WITH (FORCE)"
  else
    echo "База $DB уже есть. Для пересоздания: --recreate" >&2
    exit 1
  fi
fi

echo "== Создаю базу $DB"
"${PSQL[@]}" -d postgres -c "CREATE DATABASE \"$DB\""

echo "   0001 kassir_schema_v1.sql"
"${PSQL[@]}" -d "$DB" -f /db/kassir_schema_v1.sql

for f in /db/migrations/[0-9][0-9][0-9][0-9]_*.sql; do
  n=$(basename "$f" | cut -c1-4)
  if [ $((10#$n)) -gt "$UNTIL" ]; then
    continue
  fi
  echo "   $(basename "$f")"
  "${PSQL[@]}" -d "$DB" -f "$f"
done

echo "== Готово: $DB, schema_version по умолчанию $("${PSQL[@]}" -d "$DB" -tAc "SELECT column_default FROM information_schema.columns WHERE table_name = 'tenants' AND column_name = 'schema_version'")"
