#!/usr/bin/env bash
# Drill 4: the projector killed (SIGKILL) while it works through a backlog.
#   1. The projector is stopped; ROUNDS (default 15) rounds of raises for 3001..3012 build a backlog
#      on the business topic (one event per employee with an open salary period and round).
#   2. The projector is started and killed as soon as it has applied some, but not all, of them.
#   3. It is started again.
# Expected: every event is applied exactly once (processed events grow by the backlog size); the
# read model matches the source as before; events whose offsets were not committed before the kill
# are skipped as duplicates; a rebuild from offset 0 gives the same checksum, so the history has no
# duplicate or missing row either; nothing is dead-lettered.
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/lib.sh"

rounds=${ROUNDS:-15}

business_end() { end_offset hr.employee-pay.v1; }

# backlog_complete: succeeds when the business topic has grown and not moved for 10 s.
backlog_complete() {
  local end
  end=$(business_end)
  if [ "$end" -gt "$business_before" ] && [ "$end" = "${last_end:-}" ]; then
    return 0
  fi
  last_end=$end
  sleep 7
  return 1
}

echo "== before"
wait_for 120 "the projector to catch up" group_caught_up "$projector"
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
duplicates_before=$(log_count pay-history-projector 'Duplicate ')
dlq_before=$(end_offset hr.dlq)
business_before=$(business_end)
echo "read model: $(readmodel_checksum)"
echo "rows that differ from the source (expected: scenario 7 only):"
echo "${divergence_before:-(none)}"
if echo "$divergence_before" | grep -qE '^[<>] 3\|30(0[1-9]|1[0-2])\|'; then
  fail "rows of employees 3001..3012 differ from the source before the drill; repair with scripts/ops/backfill.sh Salary"
fi

echo "== 1. stop the projector; $rounds rounds of raises for 3001..3012"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker compose stop pay-history-projector
for _ in $(seq 1 "$rounds"); do
  raises 3001 3012 >/dev/null
done
wait_for 180 "the translator to publish the backlog" backlog_complete
backlog=$(($(business_end) - business_before))
echo "backlog on hr.employee-pay.v1: $backlog events"

echo "== 2. start the projector; kill it mid-backlog"
docker compose start pay-history-projector >/dev/null 2>&1
deadline=$((SECONDS + 120))
applied_at_kill=0
while [ "$SECONDS" -lt "$deadline" ]; do
  applied_at_kill=$(($(processed_events) - events_before))
  if [ "$applied_at_kill" -gt 0 ]; then
    docker compose kill -s SIGKILL pay-history-projector >/dev/null 2>&1
    break
  fi
done
applied_after_kill=$(($(processed_events) - events_before))
echo "killed with $applied_at_kill of $backlog applied (seen just before the kill); $applied_after_kill after it"
[ "$applied_after_kill" -lt "$backlog" ] || fail "the projector finished the backlog before the kill; run with more ROUNDS"
docker compose ps pay-history-projector --format '{{.Service}} {{.State}} {{.Status}}'
kafka_groups --describe --group "$projector" 2>/dev/null | awk 'NF && $1 != "GROUP" { print $2, $3, "committed", $4, "end", $5, "lag", $6 }'

echo "== 3. start it again"
docker compose start pay-history-projector >/dev/null 2>&1
wait_for 180 "the read model to match the source again" readmodel_back_to "$divergence_before"

echo "== after"
applied=$(($(processed_events) - events_before))
duplicates=$(($(log_count pay-history-projector 'Duplicate ') - duplicates_before))
dead_lettered=$(($(end_offset hr.dlq) - dlq_before))
echo "read model: $(readmodel_checksum)"
echo "events applied: $applied of $backlog; delivered again and skipped as duplicates: $duplicates; dead-lettered: $dead_lettered"
echo "alerts:"
print_alerts "$started"

[ "$applied" = "$backlog" ] || fail "expected $backlog events applied, found $applied"
[ "$dead_lettered" = 0 ] || fail "$dead_lettered messages dead-lettered during the drill"

echo "== rebuild from offset 0: the history must be identical"
bash scripts/ops/replay-projector.sh | grep -E '^(PASS|FAIL)'
pass "projector killed after $applied_after_kill of $backlog events: no gap, no duplicate, $duplicates redelivered events skipped"
