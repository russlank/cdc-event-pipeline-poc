# Helpers for the demo scenarios. Source from a scenario script:
#   source "$(dirname "$0")/lib.sh"
# shellcheck shell=bash

set -euo pipefail
export MSYS_NO_PATHCONV=1

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

# run_sql <file>: runs a T-SQL file against HrCore.
run_sql() {
  docker compose exec -T sqlserver bash -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d HrCore -b -W' < "$1"
}

# events: prints every business event as "<key>\t<json>", decoded through the Schema Registry.
events() {
  docker compose exec -T schema-registry kafka-avro-console-consumer \
    --bootstrap-server kafka:9092 --topic hr.employee-pay.v1 --from-beginning \
    --property schema.registry.url=http://localhost:8081 \
    --property print.key=true \
    --key-deserializer org.apache.kafka.common.serialization.StringDeserializer \
    --timeout-ms 8000 2>/dev/null | grep -E $'^[0-9]+:[0-9]+\t\\{' || true
}

# wait_for <seconds> <description> <command...>: retries the command until it succeeds.
wait_for() {
  local seconds=$1 description=$2
  shift 2
  local deadline=$((SECONDS + seconds))
  until "$@"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
      fail "timed out after ${seconds}s waiting for: $description"
    fi
    sleep 3
  done
}

# unique_ids: reads events on stdin, prints the number of distinct event ids.
unique_ids() {
  grep -oE '"eventId":"[0-9a-f-]+"' | sort -u | wc -l | tr -d ' '
}

pass() { printf 'PASS: %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
