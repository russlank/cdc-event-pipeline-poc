#!/usr/bin/env bash
# Drill 5: a column added to a captured table without a new capture instance.
#   1. dbo.Salary gets a column ApprovedBy; the capture instance dbo_Salary keeps its columns.
#   2. An update that changes only ApprovedBy (3002), and a raise for 3001.
#   3. A backfill of Salary.
#   4. The connector excludes the column (column.exclude.list); the backfill again.
#   5. Cleanup, also when the drill fails: the column is dropped and the connector configuration
#      restored.
# Expected: consumers see no new column; the update of ApprovedBy alone is not captured at all;
# the raise is published as before; the backfill fails fast (Debezium skips the table because the
# column is in the table but not in its schema) and works with the column excluded; the read model
# matches the source as before and no transaction is dead-lettered.
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/lib.sh"

connector=http://127.0.0.1:8083/connectors/hrcore-cdc
exclude='"column.exclude.list":"HrCore.dbo.Salary.ApprovedBy",'

column_present() {
  [ "$(sql_value "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Salary') AND name = N'ApprovedBy'")" = 1 ]
}

# put_config <json>: replaces the connector configuration; the task restarts.
put_config() {
  curl -fsS -X PUT -H 'Content-Type: application/json' --data "$1" "$connector/config" >/dev/null
}

config_without_exclude() {
  curl -fsS "$connector/config" | sed "s/$exclude//"
}

cleanup() {
  if column_present; then
    sql_rows "ALTER TABLE dbo.Salary DROP COLUMN ApprovedBy;" >/dev/null
    echo "cleanup: column ApprovedBy dropped"
  fi
  if curl -fsS "$connector/config" 2>/dev/null | grep -q 'column.exclude.list'; then
    put_config "$(config_without_exclude)"
    echo "cleanup: connector configuration restored"
  fi
}
trap cleanup EXIT

captured_rows_of() {
  sql_value "SELECT COUNT(*) FROM cdc.dbo_Salary_CT WHERE TenantId = 3 AND EmployeeId = $1"
}

echo "== before"
column_present && fail "dbo.Salary already has a column ApprovedBy"
wait_for 120 "the projector to catch up" group_caught_up "$projector"
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
echo "read model: $(readmodel_checksum)"
echo "rows that differ from the source (expected: scenario 7 only):"
echo "${divergence_before:-(none)}"
require_drill_rows_in_sync "$divergence_before"
echo "captured columns of dbo_Salary: $(sql_value "SELECT STRING_AGG(column_name, ',') FROM cdc.captured_columns cc
  JOIN cdc.change_tables ct ON ct.object_id = cc.object_id WHERE ct.capture_instance = 'dbo_Salary'")"

echo "== 1. add the column"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
sql_rows "ALTER TABLE dbo.Salary ADD ApprovedBy varchar(20) NULL;" >/dev/null
column_present || fail "the column was not added"

echo "== 2. update only ApprovedBy for 3002; a raise for 3001"
captured_3002=$(captured_rows_of 3002)
captured_3001=$(captured_rows_of 3001)
updated=$(sql_value "UPDATE dbo.Salary SET ApprovedBy = 'hr-lead' WHERE TenantId = 3 AND EmployeeId = 3002 AND EffectiveTo IS NULL; SELECT @@ROWCOUNT")
raises 3001 3001
raise_applied() { [ "$(processed_events)" -gt "$events_before" ]; }
wait_for 120 "the raise to reach the read model" raise_applied
sleep 10
echo "ApprovedBy update: $updated row in dbo.Salary, $(($(captured_rows_of 3002) - captured_3002)) change rows captured"
echo "raise: $(($(captured_rows_of 3001) - captured_3001)) change rows captured, $(($(processed_events) - events_before)) event applied"
columns=$(last_message cdc.HrCore.dbo.Salary | cut -f 2 | grep -oE '"after":\{[^}]*\}' | grep -oE '"[A-Za-z]+":' | tr -d '":' | sort | tr '\n' ' ')
echo "columns of the last raw Salary message: $columns"

echo "== 3. backfill of Salary with the column in the table"
if bash scripts/ops/backfill.sh Salary 2>&1 | tail -n 2; then
  fail "the backfill worked; Debezium no longer skips a table with a column its schema does not have"
fi

echo "== 4. exclude the column in the connector; backfill again"
put_config "$(curl -fsS "$connector/config" | sed "s/^{/{$exclude/")"
sleep 20
wait_for 180 "the connector task to run" task_running
bash scripts/ops/backfill.sh Salary | tail -n 1

echo "== 5. cleanup"
cleanup
trap - EXIT
sleep 20
wait_for 180 "the connector task to run" task_running
wait_for 240 "the read model to match the source again" readmodel_back_to "$divergence_before"

echo "== after"
incomplete=$(docker compose logs --since "$started" --no-log-prefix event-translator 2>/dev/null | grep -c 'IncompleteTransaction' || true)
echo "read model: $(readmodel_checksum)"
echo "events applied: $(($(processed_events) - events_before)); IncompleteTransaction dead letters: $incomplete"
docker compose logs --since "$started" --no-log-prefix connect-watchdog 2>&1 | grep restarted || true
echo "alerts:"
print_alerts "$started"

[ "$(($(captured_rows_of 3002) - captured_3002))" = 0 ] || fail "the update of ApprovedBy alone was captured"
echo "$columns" | grep -q ApprovedBy && fail "the raw message carries the new column"
[ "$incomplete" = 0 ] || fail "$incomplete messages dead-lettered as IncompleteTransaction"
pass "the new column stayed invisible downstream; the backfill failed fast with it and worked with it excluded; the read model is unchanged"
