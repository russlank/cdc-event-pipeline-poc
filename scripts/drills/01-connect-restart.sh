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

source "$(dirname "$0")/lib.sh"

down_seconds=${DOWN_SECONDS:-120}

salary_rows_sent() { [ "$(end_offset cdc.HrCore.dbo.Salary)" != "$salary_offset" ]; }

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
require_drill_rows_in_sync "$divergence_before"

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
wait_for 240 "the raises to reach the read model" readmodel_back_to "$divergence_before"
checksum_after=$(readmodel_checksum)
applied=$(($(processed_events) - events_before))
duplicates=$(($(log_count pay-history-projector 'Duplicate ') - duplicates_before))
dead_lettered=$(($(end_offset hr.dlq) - dlq_before))
echo "read model: $checksum_after"
echo "events applied: $applied; duplicates skipped by the projector: $duplicates; messages dead-lettered: $dead_lettered"
echo "translator:"
print_translator_losses "$started"
echo "alerts:"
print_alerts "$started"

[ "$applied" = 5 ] || fail "expected 5 events applied, found $applied"
[ "$dead_lettered" = 0 ] || fail "$dead_lettered messages dead-lettered during the drill"
[ "$duplicates" = 0 ] || echo "note: $duplicates duplicate events on the business topic (the projector skipped them)"
pass "Connect resumed from its stored offset; 5 raises made around the outage reached the read model once; nothing dead-lettered; no other row changed"
