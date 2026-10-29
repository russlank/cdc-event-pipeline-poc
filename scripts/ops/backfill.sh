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
wait_for 600 "the incremental snapshot of dbo.$table to finish" incremental_snapshot finished "$table" "$since"
pass "incremental snapshot of dbo.$table finished ($id)"
