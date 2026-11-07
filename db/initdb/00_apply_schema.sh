#!/bin/bash
# Первый запуск контейнера на пустом томе: схема и все миграции в базу POSTGRES_DB.
# Новые миграции подхватываются сами — docker-compose.yml править не нужно.
set -euo pipefail
bash /db/apply_schema.sh "$POSTGRES_DB"
