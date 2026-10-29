#!/usr/bin/env bash
# Re-snapshot: Debezium reads every captured table again, as after the first registration, then
# streams from the new position. Use it when the stored offset is no longer available: CDC cleanup
# removed changes Debezium had not read (scripts/checks/cdc-retention-gap.sh reports GAP, or the
# task fails with "... but this is no longer available on the server").
# Stops the connector, deletes its offsets (snapshot.mode=initial then takes a new snapshot),
# resumes it, and waits until the snapshot has completed and streaming has started.
# The snapshot rows become snapshot events: unchanged rows are duplicates, missed changes are
# restated with the snapshot time as recorded time. Changes overwritten during the gap stay lost.
#
# Usage: scripts/ops/resnapshot.sh

source "$(dirname "$0")/../scenarios/lib.sh"

connector=http://127.0.0.1:8083/connectors/hrcore-cdc

connector_state() {
  curl -fsS "$connector/status" 2>/dev/null | sed -n 's/.*"connector":{"state":"\([A-Z]*\)".*/\1/p' || true
}

stopped() { [ "$(connector_state)" = STOPPED ]; }

snapshot_streaming() {
  docker compose logs --since "$since" --no-log-prefix connect 2>&1 | grep -q 'Starting streaming'
}

# Docker reads a --since time without a zone as local time, hence the Z; look back a few seconds.
since=$(date -u -d '-5 seconds' +%Y-%m-%dT%H:%M:%SZ)

echo "== stop the connector"
curl -fsS -X PUT "$connector/stop" >/dev/null
wait_for 60 "the connector to stop" stopped

echo "== delete its offsets"
curl -fsS -X DELETE "$connector/offsets"
echo

echo "== resume: snapshot, then streaming"
curl -fsS -X PUT "$connector/resume" >/dev/null
wait_for 300 "the snapshot to complete and streaming to start" snapshot_streaming
docker compose logs --since "$since" --no-log-prefix connect 2>&1 \
  | grep -E 'Finished exporting|Snapshot completed' | sed 's/   \[.*//' | cut -c 1-160
pass "re-snapshot of hrcore-cdc completed; streaming from the new position"
