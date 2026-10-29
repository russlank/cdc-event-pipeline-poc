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

# sql_rows <query>: prints the rows a single-column query returns, one per line, no header.
sql_rows() {
  printf 'SET NOCOUNT ON;\n%s\n' "$1" | docker compose exec -T sqlserver bash -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d HrCore -b -h -1 -W' \
    | tr -d '\r' | sed '/^$/d'
}

# sql_value <query>: prints the first value of the first row a query returns.
sql_value() {
  sql_rows "$1" | sed -n '1p'
}

# readmodel_sql <sql>: runs SQL in the read model as its owner; prints rows unaligned, no header.
readmodel_sql() {
  printf '%s\n' "$1" | docker compose exec -T postgres psql -U payhistory -d payhistory -v ON_ERROR_STOP=1 -q -tA
}

# readmodel_checksum: prints "<events> events, <rows> rows, <md5>" (scripts/checks/readmodel-checksum.sql).
readmodel_checksum() {
  readmodel_sql "$(cat scripts/checks/readmodel-checksum.sql)"
}

# readmodel_divergence: prints the Salary rows of HrCore without a current pay_history row ("<"
# lines) and the current pay_history rows without a Salary row (">" lines); nothing when the read
# model matches the source. Rows dead-lettered on purpose (scenario 7) show up here too.
readmodel_divergence() {
  diff <(sql_rows "$(cat scripts/checks/source-salary-rows.sql)" | LC_ALL=C sort) \
       <(readmodel_sql "$(cat scripts/checks/readmodel-current-rows.sql)" | LC_ALL=C sort) || true
}

# kafka_groups <args...>: runs kafka-consumer-groups.sh against the broker.
kafka_groups() {
  docker compose exec -T kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 "$@"
}

# kafka_tool <tool> <args...>: runs a Kafka command-line tool against the broker.
kafka_tool() {
  docker compose exec -T kafka "/opt/kafka/bin/$1" --bootstrap-server kafka:9092 "${@:2}"
}

# last_message <topic>: prints the last message of a single-partition topic.
last_message() {
  kafka_tool kafka-console-consumer.sh --topic "$1" --from-beginning --property print.key=true \
    --timeout-ms 5000 2>/dev/null | tail -n 1
}

# end_offset <topic>: prints the sum of the end offsets of the topic's partitions.
end_offset() {
  kafka_tool kafka-get-offsets.sh --topic "$1" | awk -F: '{ sum += $3 } END { print sum }'
}

# group_caught_up <group>: succeeds when the group has committed the end of every partition it reads.
group_caught_up() {
  local rows
  rows=$(kafka_groups --describe --group "$1" 2>/dev/null | awk -v g="$1" '$1 == g')
  [ -n "$rows" ] && echo "$rows" | awk '{ if (!($6 == "0" || $5 == "0")) exit 1 }'
}

# incremental_snapshot <started|finished> <table> <since>: succeeds when Connect has logged the start
# or the end of an incremental snapshot of dbo.<table> since <since>, an ISO 8601 time with a zone
# (for example 2026-10-29T16:00:00Z).
incremental_snapshot() {
  local pattern
  case "$1" in
    started) pattern="Requested 'INCREMENTAL' snapshot of data collections '\\[HrCore\\.dbo\\.$2\\]'" ;;
    finished) pattern="incremental snapshotting of table 'HrCore\\.dbo\\.$2' finished" ;;
  esac
  docker compose logs --since "$3" connect 2>&1 | grep -qE "$pattern"
}

# log_count <service> <message-prefix>: prints how many log records of a .NET service (JSON
# console) have a message that starts with the prefix, for example 'Duplicate '.
log_count() {
  docker compose logs --no-log-prefix "$1" 2>/dev/null | grep -cF "\"Message\":\"$2" || true
}

# api <path>: GETs a Query API path and prints the body; fails on a non-2xx status.
api() {
  curl -fsS "http://127.0.0.1:5103$1"
}

# events: prints every business event as "<key>\t<json>", decoded through the Schema Registry.
# Messages that are not Avro (scenario 7 sends one) are skipped; without that the consumer stops
# at the first one, and which events it printed before depends on the partition order.
events() {
  docker compose exec -T schema-registry kafka-avro-console-consumer \
    --bootstrap-server kafka:9092 --topic hr.employee-pay.v1 --from-beginning --skip-message-on-error \
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

# iso_millis <epoch-ms>: prints the instant as ISO 8601 UTC with milliseconds.
iso_millis() {
  printf '%s.%03dZ' "$(date -u -d "@$(($1 / 1000))" +%Y-%m-%dT%H:%M:%S)" "$(($1 % 1000))"
}

pass() { printf 'PASS: %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
