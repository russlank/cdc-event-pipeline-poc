#!/usr/bin/env bash
# Scenario 7: malformed and unexpected input.
# Expected: a row with an unsupported currency (InvalidRow) and a message that is not a Debezium
# change at all (Unparseable) land in hr.dlq from the translator; a message on the business topic
# that is not Avro (Undeserializable) lands there from the projector. Both keep going: the
# translator publishes a later valid change and the projector applies it.

source "$(dirname "$0")/lib.sh"

marker="scenario-7 poison $(date -u +%Y%m%dT%H%M%SZ)"

run_sql scripts/scenarios/07-dead-letter.sql

echo "== a poison message straight onto the Salary change topic"
printf '%s\n' "$marker" | docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
  --bootstrap-server kafka:9092 --topic cdc.HrCore.dbo.Salary

echo "== a poison message straight onto the business topic"
printf '%s
' "$marker (business)" | docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh   --bootstrap-server kafka:9092 --topic hr.employee-pay.v1

echo "== a valid change afterwards: a promotion to level 3 for employee 2004"
run_sql <(printf '%s\n' "SET NOCOUNT ON; SET XACT_ABORT ON;
DECLARE @from date = DATEADD(day, 1, CAST(SYSUTCDATETIME() AS date));
IF NOT EXISTS (SELECT 1 FROM dbo.Position WHERE TenantId = 2 AND EmployeeId = 2004 AND Title = N'Team Lead')
BEGIN
  BEGIN TRANSACTION;
  UPDATE dbo.Position SET EffectiveTo = @from WHERE TenantId = 2 AND EmployeeId = 2004 AND EffectiveTo IS NULL;
  INSERT dbo.Position (TenantId, EmployeeId, Title, Level, EffectiveFrom, EffectiveTo) VALUES (2, 2004, N'Team Lead', 3, @from, NULL);
  COMMIT TRANSACTION;
END")

dead_letters() {
  docker compose exec -T kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server kafka:9092 \
    --topic hr.dlq --from-beginning --timeout-ms 8000 --property print.headers=true 2>/dev/null
}

poison_dead_lettered() {
  dead_letters | grep -F "$marker" | grep -q 'dlq.reason:Unparseable'
}

invalid_row_dead_lettered() {
  dead_letters | grep 'dlq.reason:InvalidRow' | grep 'dlq.source.topic:cdc.HrCore.dbo.Salary' | grep -q '"Currency":"GBP"'
}

business_poison_dead_lettered() {
  dead_letters | grep -F "$marker (business)" | grep 'dlq.component:pay-history-projector' | grep -q 'dlq.reason:Undeserializable'
}

promotion_projected() {
  [ "$(readmodel_sql "SELECT count(*) FROM processed_event WHERE tenant_id = 2 AND employee_id = 2004 AND event_type = 'PositionChanged' AND recorded_at > now() - interval '1 day';")" -gt 0 ]
}

promotion_published() {
  events | grep -E $'^2:2004\t' | grep '"eventType":"PositionChanged"' | grep -q '"title":"Team Lead"'
}

wait_for 60 "the poison message in hr.dlq" poison_dead_lettered
pass "poison message dead-lettered: $(dead_letters | grep -F "$marker" | grep -oE 'dlq.reason:[A-Za-z]+,dlq.error:[^,]*' | head -n 1)"
wait_for 60 "the GBP row in hr.dlq" invalid_row_dead_lettered
pass "GBP raise dead-lettered: $(dead_letters | grep '"Currency":"GBP"' | grep -oE 'dlq.reason:[A-Za-z]+,dlq.error:[^,]*' | head -n 1)"
wait_for 60 "the PositionChanged of 2:2004" promotion_published
pass "the translator kept processing: PositionChanged for 2:2004 published after both"
wait_for 60 "the business-topic poison message in hr.dlq" business_poison_dead_lettered
pass "business-topic poison dead-lettered by the projector: $(dead_letters | grep -F "$marker (business)" | grep -oE 'dlq.reason:[A-Za-z]+,dlq.error:[^,]*' | head -n 1)"
wait_for 60 "the projector to apply the PositionChanged of 2:2004" promotion_projected
pass "the projector kept processing: PositionChanged for 2:2004 applied"
