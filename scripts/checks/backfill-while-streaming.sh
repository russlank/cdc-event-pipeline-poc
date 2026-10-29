#!/usr/bin/env bash
# Backfill of dbo.Salary while the source keeps changing.
# Expected: a raise committed during the incremental snapshot is published before the snapshot
# finishes (streaming continues), and the backfill adds no pay_history rows: every row recorded
# since the backfill started belongs to that raise.

source "$(dirname "$0")/../scenarios/lib.sh"

start=$(sql_value "SELECT CONVERT(varchar(27), SYSUTCDATETIME(), 126);")
since=$(date -u -d '-5 seconds' +%Y-%m-%dT%H:%M:%SZ)
duplicates_before=$(log_count pay-history-projector 'Duplicate ')
echo "read model before: $(readmodel_checksum)"

bash scripts/ops/backfill.sh Salary &
backfill=$!

wait_for 60 "the incremental snapshot to start" incremental_snapshot started Salary "$since"
run_sql scripts/checks/backfill-concurrent-raise.sql
salary_id=$(sql_value "SELECT MAX(SalaryId) FROM dbo.Salary WHERE TenantId = 2 AND EmployeeId = 2005;")
incremental_snapshot finished Salary "$since" && fail "the snapshot finished before the raise; nothing ran concurrently"

wait "$backfill" || fail "backfill failed"
finished_at=$(docker compose logs --since "$since" connect 2>&1 \
  | grep -E "incremental snapshotting of table 'HrCore\.dbo\.Salary' finished" | tail -n 1 \
  | grep -oE '[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{8}')
finished_ms=$(( $(date -u -d "$finished_at" +%s) * 1000 + 999 ))

raise_event() {
  raise=$(docker compose exec -T schema-registry kafka-avro-console-consumer \
    --bootstrap-server kafka:9092 --topic hr.employee-pay.v1 --from-beginning \
    --property schema.registry.url=http://localhost:8081 --property print.timestamp=true \
    --timeout-ms 8000 2>/dev/null | grep "\"salaryId\":$salary_id," | grep '"snapshot":false' | head -n 1)
  [ -n "$raise" ]
}

wait_for 60 "the SalaryChanged of salary $salary_id" raise_event
published_ms=$(echo "$raise" | grep -oE '^CreateTime:[0-9]+' | grep -oE '[0-9]+')
[ "$published_ms" -le "$finished_ms" ] \
  || fail "the raise was published at $published_ms ms, after the snapshot finished ($finished_at)"
pass "raise (salary $salary_id) published while the backfill ran; the snapshot finished at $finished_at"

wait_for 120 "the projector to catch up" group_caught_up cdcpay-pay-history-projector
recorded=$(readmodel_sql "SELECT count(*) || ' ' || count(DISTINCT recorded_from) FROM pay_history WHERE recorded_from >= '${start}Z';")
[ "$recorded" = "2 1" ] || fail "rows recorded since the backfill started: '$recorded' (count, instants); expected the raise's 2 rows at 1 instant"
duplicates=$(( $(log_count pay-history-projector 'Duplicate ') - duplicates_before ))
echo "read model after: $(readmodel_checksum); $duplicates backfill events were duplicates"
pass "the backfill added no pay_history rows; only the concurrent raise did"
