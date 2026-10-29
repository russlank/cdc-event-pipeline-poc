#!/usr/bin/env bash
# Drill 3: Kafka Connect down for longer than the CDC retention.
#   1. Connect is stopped; raises for 3001 and 3002 are committed (one transaction each).
#   2. The cleanup retention is lowered to 1 minute; after 70 s a raise for 3003 is committed and the
#      cleanup job runs. Retention counts back from the newest change, and cleanup keeps the newest
#      transaction before the boundary, so it removes 3001's raise, which Debezium has not read.
#   3. scripts/checks/cdc-retention-gap.sh must report the gap; the retention is set back.
#   4. Connect is started: the task must fail ("no longer available on the server"), and the
#      watchdog must leave it failed. Nothing of the raises reaches the read model.
#   5. scripts/ops/resnapshot.sh repairs it.
# Expected: the loss is detected before and at the restart; after the re-snapshot the read model
# matches the source as before, nothing is dead-lettered, and the gap check passes.
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/lib.sh"

retention_default=4320

set_retention() {
  sql_rows "EXEC sys.sp_cdc_change_job @job_type = N'cleanup', @retention = $1;" >/dev/null
  echo "cleanup retention: $(sql_value "SELECT retention FROM msdb.dbo.cdc_jobs WHERE job_type = 'cleanup'") minutes"
}

# The retention must not stay at 1 minute, also when the drill fails.
trap 'set_retention "$retention_default" >/dev/null' EXIT

# drill_rows_kept: prints how many of the drill raises' change rows are still in the change table.
drill_rows_kept() {
  sql_value "SELECT COUNT(*) FROM cdc.dbo_Salary_CT WHERE TenantId = 3 AND EmployeeId IN (3001, 3002, 3003)"
}

last_cleanup_run() {
  sql_value "SELECT MAX(instance_id) FROM msdb.dbo.sysjobhistory h JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
    WHERE j.name = 'cdc.HrCore_cleanup' AND h.step_id = 0"
}

cleanup_ran() { [ "$(last_cleanup_run)" != "$cleanup_before" ]; }
task_failed() { [ "$(task_state)" = FAILED ]; }

echo "== before"
echo "task: $(task_state)"
wait_for 120 "the projector to catch up" group_caught_up "$projector"
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
dlq_before=$(end_offset hr.dlq)
echo "read model: $(readmodel_checksum)"
echo "rows that differ from the source (expected: scenario 7 only):"
echo "${divergence_before:-(none)}"
require_drill_rows_in_sync "$divergence_before"
bash scripts/checks/cdc-retention-gap.sh | tail -n 1

echo "== 1. stop Connect; raises for 3001 and 3002"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker compose stop connect
raises 3001 3002

echo "== 2. retention 1 minute; after 70 s a raise for 3003; run the cleanup job"
set_retention 1
sleep 70
raises 3003 3003
sleep 5
kept_before=$(drill_rows_kept)
cleanup_before=$(last_cleanup_run)
sql_rows "EXEC msdb.dbo.sp_start_job N'cdc.HrCore_cleanup'"
wait_for 120 "the cleanup job to finish" cleanup_ran
echo "change rows of the drill raises: $kept_before before the cleanup, $(drill_rows_kept) after"

echo "== 3. gap check before the restart"
bash scripts/checks/cdc-retention-gap.sh && fail "the gap check did not report the removed changes" || true
set_retention "$retention_default"

echo "== 4. start Connect"
restarted=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker compose start connect
wait_for 180 "the connector task to fail" task_failed
curl -fsS localhost:8083/connectors/hrcore-cdc/status | grep -oE 'but this is no longer available on the server[^"]{0,60}' | head -n 1
sleep 30
echo "task 30 s later: $(task_state)"
docker compose logs --since "$restarted" --no-log-prefix connect-watchdog 2>&1 | grep 'task' || true
[ "$(task_state)" = FAILED ] || fail "the task did not stay failed"
applied_during=$(($(processed_events) - events_before))
echo "events applied so far: $applied_during"

echo "== 5. re-snapshot"
resnapshot_started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
bash scripts/ops/resnapshot.sh
wait_for 300 "the read model to match the source again" readmodel_back_to "$divergence_before"
# The heartbeat transaction open at the stop never gets its END; the translator gives it up 60 s
# after the snapshot's first transaction. Its row has no pay meaning, so it must not be dead-lettered.
wait_for 120 "the translator to give up the interrupted heartbeat transaction" \
  bash -c "docker compose logs --since $resnapshot_started --no-log-prefix event-translator | grep -q '\"Message\":\"Abandoned transaction'"

echo "== after"
applied=$(($(processed_events) - events_before))
# Every snapshot dead-letters scenario 7's rejected row again (InvalidRow); anything else is a loss.
dead_lettered=$(($(end_offset hr.dlq) - dlq_before))
incomplete=$(docker compose logs --since "$started" --no-log-prefix event-translator 2>/dev/null | grep -c 'IncompleteTransaction' || true)
recorded=$(readmodel_sql "SET TIME ZONE 'UTC'; SELECT to_char(max(recorded_from), 'HH24:MI:SS') FROM pay_history
  WHERE tenant_id = 3 AND employee_id IN (3001, 3002, 3003) AND recorded_to IS NULL")
echo "read model: $(readmodel_checksum)"
echo "events applied: $applied (snapshot events for new or changed rows); messages dead-lettered: $dead_lettered, $incomplete of them IncompleteTransaction"
echo "recorded time of the restored raises: $recorded (the snapshot, not the source commit)"
echo "translator:"
print_translator_losses "$started"
echo "alerts:"
print_alerts "$started"
bash scripts/checks/cdc-retention-gap.sh | tail -n 1

[ "$applied_during" = 0 ] || fail "$applied_during events were applied before the re-snapshot"
[ "$incomplete" = 0 ] || fail "$incomplete messages dead-lettered as IncompleteTransaction during the drill"
pass "loss past the CDC retention detected by the gap check and by the failed task; the re-snapshot restored the read model"
