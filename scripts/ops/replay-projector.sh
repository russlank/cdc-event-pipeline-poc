#!/usr/bin/env bash
# Rebuilds the read model from the business topic and proves the rebuild identical:
#   1. wait until the projector has caught up, take the read-model checksum;
#   2. stop the projector;
#   3. empty the read model (pay_history, processed_event);
#   4. reset the projector's consumer group to the earliest offsets;
#   5. start the projector and wait until it has caught up again;
#   6. take the checksum again; it must equal the first.
# Run while the source is quiet: a change committed during the replay changes the checksum.
# The Query API answers from a partial read model until step 5 is done.
#
# Usage: scripts/ops/replay-projector.sh

source "$(dirname "$0")/../scenarios/lib.sh"

group=cdcpay-pay-history-projector
topic=hr.employee-pay.v1

echo "== 1. checksum before"
wait_for 120 "group $group to catch up" group_caught_up "$group"
before=$(readmodel_checksum)
echo "$before"

echo "== 2. stop the projector"
docker compose stop pay-history-projector

echo "== 3. empty the read model"
readmodel_sql "TRUNCATE pay_history, processed_event RESTART IDENTITY;"
readmodel_checksum

echo "== 4. reset $group to the earliest offsets of $topic"
reset_group() {
  kafka_groups --group "$group" --topic "$topic" --reset-offsets --to-earliest --execute 2>&1 | grep -q "^$group *$topic "
}
wait_for 60 "group $group to be inactive and reset" reset_group

echo "== 5. start the projector and wait until it has caught up"
docker compose start pay-history-projector
wait_for 300 "group $group to catch up" group_caught_up "$group"

echo "== 6. checksum after"
after=$(readmodel_checksum)
echo "$after"
[ "$before" = "$after" ] || fail "read model differs after the replay: before '$before', after '$after'"
pass "read model rebuilt from offset 0; checksum identical ($after)"
