#!/usr/bin/env bash
# Drill 7: poison messages of kinds scenario 7 does not send, then the alert.
#   1. On cdc.HrCore.dbo.Employee: JSON that is not a Debezium change.
#   2. On cdc.HrCore.dbo.Salary: a tombstone (null value).
#   3. On cdc.transaction: a transaction marker without its fields.
#   4. On hr.employee-pay.v1: bytes in the Avro wire format with a schema id the registry does not
#      know.
#   5. A raise for 3001 afterwards.
# Every poison message carries row-like content ("Sentinel Officer", 98765.43) that must not show
# up in a dlq.error header or a log line.
# Expected: each poison message is dead-lettered with its reason and component, or dropped for a
# stated reason; translator and projector keep going (the raise is applied); DlqNotEmpty fires and
# resolves once nothing more is dead-lettered for 5 minutes (at most RESOLVE_SECONDS, default 600).
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/lib.sh"

resolve_seconds=${RESOLVE_SECONDS:-600}
sentinel="Sentinel Officer"
tag="drill-7-$(date -u +%Y%m%dT%H%M%SZ)"

# produce <topic> [producer properties...]: sends stdin lines as messages, keyed by $tag.
produce() {
  local topic=$1
  shift
  sed "s/^/$tag|/" | docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
    --bootstrap-server kafka:9092 --topic "$topic" \
    --reader-property parse.key=true --reader-property key.separator='|' "$@" 2>/dev/null
}

# drill_dead_letters: prints the dead letters of this drill with their headers, one per line. The
# Avro-like poison message has NUL bytes, hence grep -a.
drill_dead_letters() {
  kafka_tool kafka-console-consumer.sh --topic hr.dlq --from-beginning --timeout-ms 8000 \
    --formatter-property print.headers=true --formatter-property print.key=true 2>/dev/null \
    | grep -aF "$tag" | tr -d '\000' || true
}

header() { grep -oE "$1:[^,]*" | head -n 1 | cut -d: -f2-; }

stalled_alert() { alert_logged "$1" DlqNotEmpty "$started"; }

echo "== before"
wait_for 120 "the projector to catch up" group_caught_up "$projector"
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
dlq_before=$(end_offset hr.dlq)
echo "read model: $(readmodel_checksum); hr.dlq end offset $dlq_before"
require_drill_rows_in_sync "$divergence_before"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)

echo "== 1..4. poison messages (key $tag)"
printf '{"name":"%s","salary":98765.43}\n' "$sentinel" | produce cdc.HrCore.dbo.Employee
printf '<NULL>\n' | produce cdc.HrCore.dbo.Salary --reader-property null.marker='<NULL>'
printf '{"status":"END","id":"%s"}\n' "$sentinel" | produce cdc.transaction
printf '\000\000\017\102\077%s 98765.43\n' "$sentinel" | produce hr.employee-pay.v1

echo "== 5. a raise for 3001"
raises 3001 3001
raise_applied() { [ "$(processed_events)" -gt "$events_before" ]; }
wait_for 120 "the raise to reach the read model" raise_applied
wait_for 120 "the read model to match the source" readmodel_back_to "$divergence_before"
echo "raise applied; read model: $(readmodel_checksum)"

echo "== dead letters of the drill"
sleep 5
letters=$(drill_dead_letters)
echo "$letters" | while IFS= read -r letter; do
  [ -n "$letter" ] || continue
  printf '%-22s %-22s %-18s %s\n' "$(echo "$letter" | header dlq.component)" "$(echo "$letter" | header dlq.source.topic)" \
    "$(echo "$letter" | header dlq.reason)" "$(echo "$letter" | header dlq.error | cut -c 1-90)"
done
dead_lettered=$(($(end_offset hr.dlq) - dlq_before))
echo "hr.dlq grew by $dead_lettered"
echo "translator and projector log lines of the drill:"
docker compose logs --since "$started" --no-log-prefix event-translator pay-history-projector 2>/dev/null \
  | grep -oE '"Message":"[^"]*"' | grep -vE '"Message":"(Published|Applied|SpanId)' | cut -c 1-170 | sort -u || true
docker compose ps event-translator pay-history-projector --format '{{.Service}}: {{.Status}}'

echo "== DlqNotEmpty"
wait_for 180 "DlqNotEmpty to fire" stalled_alert FIRING
echo "fired"
wait_for "$resolve_seconds" "DlqNotEmpty to resolve" stalled_alert RESOLVED
echo "resolved"
print_alerts "$started"

headers_with_sentinel=$(echo "$letters" | grep -oE 'dlq\.error:[^,]*' | grep -cF "$sentinel" || true)
logs_with_sentinel=$(docker compose logs --since "$started" event-translator pay-history-projector 2>/dev/null | grep -cF "$sentinel" || true)
echo "sentinel in dlq.error headers: $headers_with_sentinel; in log lines: $logs_with_sentinel"
[ "$headers_with_sentinel" = 0 ] || fail "a dlq.error header quotes message content"
[ "$logs_with_sentinel" = 0 ] || fail "a log line quotes message content"
for topic in cdc.HrCore.dbo.Employee cdc.HrCore.dbo.Salary hr.employee-pay.v1; do
  echo "$letters" | grep -q "dlq.source.topic:$topic" || fail "the poison message on $topic was not dead-lettered"
done
pass "poison messages dead-lettered with reason and component, nothing quoted; both services kept going; DlqNotEmpty fired and resolved"
