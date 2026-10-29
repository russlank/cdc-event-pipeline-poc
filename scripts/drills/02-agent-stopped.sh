#!/usr/bin/env bash
# Drill 2: SQL Server Agent stopped while the source changes, then started again.
#   1. SQL Server Agent is stopped; the CDC capture job runs in it.
#   2. Raises for 3001..3002 are committed, and the drill waits until CdcEventsStalled fires
#      (at most STALL_SECONDS, default 420), checking that nothing reaches the read model.
#   3. SQL Server Agent is started again.
# Expected: capture stops (the raises reach neither the change tables nor Kafka) while the
# connector task keeps running; CdcEventsStalled fires; after the start the capture job catches up,
# the two raises are applied once with their source commit time as recorded time, nothing is
# dead-lettered, and CdcEventsStalled resolves.
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/lib.sh"

stall_seconds=${STALL_SECONDS:-420}

# agent <STOP|START>: stops or starts SQL Server Agent.
agent() {
  sql_rows "EXEC master.dbo.xp_servicecontrol N'$1', N'SQLServerAGENT'"
}

agent_state() {
  sql_value "SELECT status_desc FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server Agent%'"
}

# since_last_event: prints Debezium's seconds since its last change event, as Prometheus has it.
since_last_event() {
  curl -fsS localhost:9090/api/v1/query \
    --data-urlencode 'query=max(debezium_metrics_millisecondssincelastevent{context="streaming"}) / 1000' \
    | sed -n 's/.*"value":\[[0-9.]*,"\([0-9.]*\)"\].*/\1/p' | cut -d. -f1
}

# captured_salary_rows: prints the number of rows in the Salary change tables.
captured_salary_rows() {
  sql_value "DECLARE @n int = 0, @sql nvarchar(max) = N'';
    SELECT @sql += N'SELECT @n += COUNT(*) FROM cdc.' + QUOTENAME(OBJECT_NAME(object_id)) + N';'
    FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dbo.Salary');
    EXEC sp_executesql @sql, N'@n int OUTPUT', @n OUTPUT; SELECT @n;"
}

stalled_alert_fired() { alert_logged FIRING CdcEventsStalled "$started"; }
stalled_alert_resolved() { alert_logged RESOLVED CdcEventsStalled "$restarted"; }

echo "== before"
echo "SQL Server Agent: $(agent_state); task: $(task_state); seconds since last event: $(since_last_event)"
wait_for 120 "the projector to catch up" group_caught_up "$projector"
checksum_before=$(readmodel_checksum)
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
dlq_before=$(end_offset hr.dlq)
salary_topic_before=$(end_offset cdc.HrCore.dbo.Salary)
captured_before=$(captured_salary_rows)
echo "read model: $checksum_before"
echo "rows that differ from the source (expected: scenario 7 only):"
echo "${divergence_before:-(none)}"
require_drill_rows_in_sync "$divergence_before"

echo "== 1. stop SQL Server Agent"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
agent STOP
echo "SQL Server Agent: $(agent_state)"

echo "== 2. raises for 3001..3002; wait for CdcEventsStalled (at most ${stall_seconds}s)"
raises 3001 3002
committed=$(date -u +%s)
wait_for "$stall_seconds" "CdcEventsStalled to fire" stalled_alert_fired
echo "CdcEventsStalled fired after $(($(date -u +%s) - committed)) s; seconds since last event: $(since_last_event)"
captured_during=$(($(captured_salary_rows) - captured_before))
sent_during=$(($(end_offset cdc.HrCore.dbo.Salary) - salary_topic_before))
applied_during=$(($(processed_events) - events_before))
echo "while stopped: $captured_during rows captured, $sent_during Salary messages sent, $applied_during events applied; task: $(task_state)"
docker compose logs --since "$started" --no-log-prefix connect 2>&1 | grep -iE 'agent' | sort -u -k 3 | cut -c 1-200 | head -5 || true

echo "== 3. start SQL Server Agent"
restarted=$(date -u +%Y-%m-%dT%H:%M:%SZ)
agent START
echo "SQL Server Agent: $(agent_state)"
wait_for 240 "the raises to reach the read model" readmodel_back_to "$divergence_before"
echo "caught up $(($(date -u +%s) - $(date -u -d "$restarted" +%s))) s after the start"
wait_for 300 "CdcEventsStalled to resolve" stalled_alert_resolved

echo "== after"
checksum_after=$(readmodel_checksum)
applied=$(($(processed_events) - events_before))
dead_lettered=$(($(end_offset hr.dlq) - dlq_before))
recorded=$(readmodel_sql "SELECT max(extract(epoch FROM recorded_from))::bigint FROM pay_history
  WHERE tenant_id = 3 AND employee_id IN (3001, 3002) AND recorded_to IS NULL")
echo "read model: $checksum_after"
echo "events applied: $applied; messages dead-lettered: $dead_lettered"
echo "latest recorded time of the raises: $(date -u -d "@$recorded" +%H:%M:%SZ); committed by $(date -u -d "@$committed" +%H:%M:%SZ); Agent started $restarted"
echo "translator:"
print_translator_losses "$started"
echo "alerts:"
print_alerts "$started"

[ "$captured_during" = 0 ] || fail "$captured_during Salary rows were captured while SQL Server Agent was stopped"
[ "$sent_during" = 0 ] || fail "$sent_during Salary messages were sent while SQL Server Agent was stopped"
[ "$applied" = 2 ] || fail "expected 2 events applied, found $applied"
[ "$dead_lettered" = 0 ] || fail "$dead_lettered messages dead-lettered during the drill"
[ "$recorded" -le "$committed" ] || fail "the recorded time is the capture time, not the source commit time"
pass "capture stopped with SQL Server Agent and CdcEventsStalled fired; after the start the 2 raises were applied once with their commit time; the alert resolved"
