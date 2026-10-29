#!/usr/bin/env bash
# Scenario 6: the same changes replayed.
# Expected: replaying business events (projector group moved back) and raw CDC events
# (translator group reset to the start, so every event is published again with the same id)
# leaves the read model unchanged: same event and row counts, same checksum. The projector
# reports the replayed events as duplicates.
# Run while the source is quiet: a change committed meanwhile changes the checksum.

source "$(dirname "$0")/lib.sh"

projector=cdcpay-pay-history-projector
translator=cdcpay-event-translator

# reset_group <group> <reset args...>: resets the offsets of a stopped group.
reset_group() {
  local group=$1
  shift
  kafka_groups --group "$group" --reset-offsets "$@" --execute 2>&1 | grep -q "^$group "
}

wait_for 120 "the projector to catch up" group_caught_up "$projector"
before=$(readmodel_checksum)
duplicates_before=$(log_count pay-history-projector 'Duplicate ')
echo "read model before: $before"

echo "== replay business events: move the projector back 25 events per partition"
docker compose stop pay-history-projector
wait_for 60 "group $projector to be reset" reset_group "$projector" --topic hr.employee-pay.v1 --shift-by -25
docker compose start pay-history-projector
wait_for 120 "the projector to catch up" group_caught_up "$projector"
after_business=$(readmodel_checksum)
[ "$after_business" = "$before" ] || fail "read model changed after the business replay: $after_business"
pass "business replay: read model unchanged ($after_business)"

echo "== replay raw events: reset the translator to the start of every raw topic"
published_before=$(log_count event-translator 'Published ')
docker compose stop event-translator
wait_for 60 "group $translator to be reset" reset_group "$translator" --all-topics --to-earliest
docker compose start event-translator
republished() { [ $(( $(log_count event-translator 'Published ') - published_before )) -gt 0 ]; }
wait_for 180 "the translator to publish again" republished
wait_for 300 "the translator to catch up" group_caught_up "$translator"
wait_for 300 "the projector to catch up" group_caught_up "$projector"
after_raw=$(readmodel_checksum)
[ "$after_raw" = "$before" ] || fail "read model changed after the raw replay: $after_raw"

duplicates=$(( $(log_count pay-history-projector 'Duplicate ') - duplicates_before ))
[ "$duplicates" -gt 0 ] || fail "the projector reported no duplicates"
pass "raw replay: read model unchanged ($after_raw); the projector skipped $duplicates duplicate events"
