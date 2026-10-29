#!/usr/bin/env bash
# Drill 1: Kafka Connect stopped while the source changes, then started again.
#   1. A raise for 3001 is committed and Connect is stopped as soon as Debezium has sent its rows,
#      usually before the END of its transaction (Debezium sends an END when the next transaction
#      begins).
#   2. Raises for 3002..3005 are made while Connect is down for DOWN_SECONDS (default 120, longer
#      than the translator's 60 s transaction timeout).
#   3. Connect is started again.
# Expected: the task runs again without help (connect-watchdog restarts it after the known Debezium
# failure); Debezium resumes from the offset it stored at the stop; the five raises reach the read
# model once each (five events applied; the read model matches the source as before); nothing is
# dead-lettered. Alerts during the drill are printed.
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/../scenarios/lib.sh"

down_seconds=${DOWN_SECONDS:-120}
translator=cdcpay-event-translator
projector=cdcpay-pay-history-projector

# raises <first> <last>: commits a raise for each employee <first>..<last> of tenant 3.
raises() {
  run_sql <(printf ':setvar First %s\n:setvar Last %s\n' "$1" "$2"; cat scripts/drills/01-changes.sql)
}

# task_state: prints the state of the connector's task (RUNNING, FAILED, ...), nothing while
# Connect does not answer.
task_state() {
  curl -fsS localhost:8083/connectors/hrcore-cdc/status 2>/dev/null \
    | sed -n 's/.*"tasks":\[{"id":0,"state":"\([A-Z]*\)".*/\1/p' || true
}

task_running() { [ "$(task_state)" = RUNNING ]; }

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

salary_rows_sent() { [ "$(end_offset cdc.HrCore.dbo.Salary)" != "$salary_offset" ]; }

# processed_events: prints the number of events the projector has applied.
processed_events() {
  readmodel_sql 'SELECT count(*) FROM processed_event'
}

# The translator's group is not waited for: its offset on the heartbeat topic stays one behind
# while the latest heartbeat transaction waits for its END.
caught_up() {
  group_caught_up "$projector" && [ "$(readmodel_divergence)" = "$divergence_before" ]
}

echo "== before"
docker compose ps --format 'table {{.Service}}\t{{.Status}}'
echo "task: $(task_state)"
kafka_groups --describe --group "$translator" --group "$projector" 2>/dev/null | awk 'NF && $1 != "GROUP" { print $1, $2, $3, "lag", $6 }'
wait_for 120 "the projector to catch up" group_caught_up "$projector"
checksum_before=$(readmodel_checksum)
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
duplicates_before=$(log_count pay-history-projector 'Duplicate ')
dlq_before=$(end_offset hr.dlq)
echo "read model: $checksum_before"
echo "rows that differ from the source (expected: scenario 7 only):"
echo "${divergence_before:-(none)}"
if echo "$divergence_before" | grep -qE '^[<>] 3\|300[1-5]\|'; then
  fail "rows of employees 3001..3005 differ from the source before the drill; repair with scripts/ops/backfill.sh Salary"
fi

echo "== 1. raise for 3001, stop Connect once its rows are sent"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
salary_offset=$(end_offset cdc.HrCore.dbo.Salary)
raises 3001 3001
wait_for 30 "Debezium to send the rows of the raise" salary_rows_sent
docker compose stop connect
echo "last transaction marker: $(last_message cdc.transaction | cut -f 2 | cut -c 1-80)"
offset_at_stop=$(last_message _connect-offsets | cut -f 2)
echo "stored offset: $offset_at_stop"
if echo "$offset_at_stop" | grep -q 'transaction_data_collection_order_HrCore.dbo.Salary'; then
  echo "the raise's transaction was open at the stop (rows sent, no END)"
else
  echo "a heartbeat closed the raise's transaction before the stop; the drill still checks the outage"
fi

echo "== 2. raises for 3002..3005 while Connect is down; wait ${down_seconds}s"
raises 3002 3005
sleep "$down_seconds"

echo "== 3. start Connect"
restarted=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker compose start connect
wait_for 240 "the connector task to run" task_running
sleep 30
wait_for 120 "the connector task to run" task_running
docker compose logs --since "$restarted" --no-log-prefix connect 2>&1 \
  | grep -E 'Last position recorded in offsets|Task threw an uncaught' | cut -c 1-200 || true
docker compose logs --since "$restarted" --no-log-prefix connect-watchdog 2>&1 | grep restarted || true

echo "== after"
wait_for 240 "the raises to reach the read model" caught_up
checksum_after=$(readmodel_checksum)
applied=$(($(processed_events) - events_before))
duplicates=$(($(log_count pay-history-projector 'Duplicate ') - duplicates_before))
dead_lettered=$(($(end_offset hr.dlq) - dlq_before))
echo "read model: $checksum_after"
echo "events applied: $applied; duplicates skipped by the projector: $duplicates; messages dead-lettered: $dead_lettered"
echo "translator:"
docker compose logs --since "$started" --no-log-prefix event-translator 2>/dev/null \
  | grep -oE '"Message":"(Abandoned|Dead-lettered)[^"]*"' | cut -c 1-160 || true
echo "alerts:"
docker compose logs --since "$started" --no-log-prefix alert-sink 2>&1 | grep -E '^ALERT ' | cut -c 1-160 || true

[ "$applied" = 5 ] || fail "expected 5 events applied, found $applied"
[ "$dead_lettered" = 0 ] || fail "$dead_lettered messages dead-lettered during the drill"
[ "$duplicates" = 0 ] || echo "note: $duplicates duplicate events on the business topic (the projector skipped them)"
pass "Connect resumed from its stored offset; 5 raises made around the outage reached the read model once; nothing dead-lettered; no other row changed"
