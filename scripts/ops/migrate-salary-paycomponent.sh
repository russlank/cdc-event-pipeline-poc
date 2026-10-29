#!/usr/bin/env bash
# Online source schema change: add Salary.PayComponent without stopping CDC.
# SQL Server CDC does not pick up new columns, so the change needs a second capture
# instance (at most two per table) and a switch-over:
#   1. check that contract v2 (payComponent) is registered, so the translator can publish it;
#   2. add the column and the capture instance dbo_Salary_v2 (db/migrations/001-...sql);
#   3. wait until Debezium has migrated to dbo_Salary_v2;
#   4. drop the old capture instance dbo_Salary (...-cleanup.sql);
#   5. wait until the connector runs again: Debezium still lists the dropped change table,
#      fails once on it, and restarts its task after about 10 s (a retriable error).
# Idempotent: finished steps are skipped.
#
# Usage: scripts/ops/migrate-salary-paycomponent.sh

source "$(dirname "$0")/../scenarios/lib.sh"

connector_running() {
  status=$(curl -s http://127.0.0.1:8083/connectors/hrcore-cdc/status)
  echo "$status" | grep -q '"connector":{"state":"RUNNING"' && ! echo "$status" | grep -qE '"state":"(FAILED|UNASSIGNED|PAUSED)"'
}

echo "== 1. contract v2 registered?"
curl -s http://127.0.0.1:8081/subjects/hr.employee-pay.v1-value/versions/latest | grep -q payComponent \
  || fail "the latest registered contract has no payComponent; run schemas-init with contracts/avro/v2 first"
pass "latest registered contract has payComponent"

echo "== 2. add Salary.PayComponent and capture instance dbo_Salary_v2"
run_sql db/migrations/001-salary-paycomponent.sql

echo "== 3. wait for Debezium to migrate to dbo_Salary_v2"
migrated() { docker compose logs connect 2>&1 | grep -q 'Migrating schema to Capture instance "dbo_Salary_v2"'; }
wait_for 120 "Debezium migrating to dbo_Salary_v2" migrated
pass "Debezium streams dbo.Salary from dbo_Salary_v2"

echo "== 4. drop the old capture instance dbo_Salary"
run_sql db/migrations/001-salary-paycomponent-cleanup.sql

echo "== 5. wait for the connector to run again"
sleep 15
wait_for 120 "connector hrcore-cdc RUNNING" connector_running
pass "connector RUNNING; migration complete"
