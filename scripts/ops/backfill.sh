#!/usr/bin/env bash
# Backfill: re-reads one source table through a Debezium incremental snapshot, without stopping
# streaming. Inserts an execute-snapshot signal into dbo.DebeziumSignal and waits until Connect
# reports the snapshot finished. The rows arrive on the table's topic with "snapshot":"incremental";
# the translator turns them into snapshot events whose ids depend on the row content, so the
# projector sees unchanged rows as duplicates and changed ones as facts it already holds.
#
# Usage: scripts/ops/backfill.sh <Tenant|Employee|Position|Salary>

source "$(dirname "$0")/../scenarios/lib.sh"

table=${1:-}
case "$table" in
  Tenant | Employee | Position | Salary) ;;
  *) fail "usage: scripts/ops/backfill.sh <Tenant|Employee|Position|Salary>" ;;
esac

# Docker reads a --since time without a zone as local time, hence the Z; the clocks can differ
# slightly, so look back a few seconds.
since=$(date -u -d '-5 seconds' +%Y-%m-%dT%H:%M:%SZ)
id="backfill-$table-$(date -u +%Y%m%dT%H%M%SZ)"

echo "== signal $id: incremental snapshot of dbo.$table"
sql_rows "INSERT dbo.DebeziumSignal (id, type, data)
  VALUES ('$id', 'execute-snapshot', '{\"data-collections\": [\"HrCore.dbo.$table\"], \"type\": \"incremental\"}');" >/dev/null

wait_for 60 "Connect to start the incremental snapshot of dbo.$table" incremental_snapshot started "$table" "$since"
echo "started; streaming continues while the table is read in chunks"
# Debezium logs a skipped table only as a warning and goes on streaming; that snapshot never finishes.
snapshot_ended() {
  incremental_snapshot finished "$table" "$since" || incremental_snapshot failed "$table" "$since"
}
wait_for 600 "the incremental snapshot of dbo.$table to finish" snapshot_ended
if incremental_snapshot failed "$table" "$since"; then
  docker compose logs --since "$since" --no-log-prefix connect 2>&1 \
    | grep -A1 "Error while executing incremental snapshot for table 'HrCore.dbo.$table'" | tail -n 1 | cut -c 1-200
  fail "Debezium skipped the incremental snapshot of dbo.$table (cause above); a column that the capture instance does not have is a known cause, see docs/operations.md"
fi
pass "incremental snapshot of dbo.$table finished ($id)"
