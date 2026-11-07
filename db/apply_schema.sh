#!/bin/bash
# =====================================================================
# Накатывает на существующую пустую базу схему v1 (миграция 0001) и все
# db/migrations/NNNN_*.sql по порядку. Используется в create_database.sh и
# при инициализации тома Docker (db/initdb/00_apply_schema.sh).
#
#   bash /db/apply_schema.sh <база> [--until NNNN]
# =====================================================================
set -euo pipefail

DB=${1:?Укажите имя базы}
shift
UNTIL=9999
while [ $# -gt 0 ]; do
  case "$1" in
    --until) UNTIL=$((10#$2)); shift ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
  esac
  shift
done

PSQL=(psql -U postgres -X -q -v ON_ERROR_STOP=1 -d "$DB")

echo "   0001 kassir_schema_v1.sql"
"${PSQL[@]}" -f /db/kassir_schema_v1.sql

for f in /db/migrations/[0-9][0-9][0-9][0-9]_*.sql; do
  n=$(basename "$f" | cut -c1-4)
  if [ $((10#$n)) -gt "$UNTIL" ]; then
    continue
  fi
  echo "   $(basename "$f")"
  "${PSQL[@]}" -f "$f"
done
