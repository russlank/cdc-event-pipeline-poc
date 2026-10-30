#!/usr/bin/env bash
# Pipeline walkthrough: the whole pipeline in one guided run, about three minutes once the images
# are built.
#   1. Start the stack (with --fresh: reset it first, docker compose down -v).
#   2. The initial snapshot: every seeded employee as an event, the read model equal to HrCore.
#   3. A retroactive raise (scenario 3): recorded today, effective three months ago.
#   4. What it produced: the business event, its schema in the registry, and the as-of queries
#      "salary on that date as known now" and "as known just before the raise".
#   5. A failure: poison messages (drill 7) go to hr.dlq, and the DlqNotEmpty alert fires.
#   6. Where to look next: Grafana, Prometheus, Alertmanager, Kafka UI.
# Options: --fresh resets the stack first; --pause waits for Enter between the steps (for
# presenting). Needs .env (see .env.example). See docs/walkthrough.md.

set -euo pipefail
cd "$(dirname "$0")/.."

fresh=0
pause=0
for argument in "$@"; do
  case "$argument" in
    --fresh) fresh=1 ;;
    --pause) pause=1 ;;
    *) echo "usage: scripts/walkthrough.sh [--fresh] [--pause]" >&2; exit 2 ;;
  esac
done

step=0
started=$SECONDS

# section <title>: starts a step; with --pause, waits for Enter first.
section() {
  step=$((step + 1))
  if [ "$pause" = 1 ] && [ "$step" -gt 1 ]; then
    read -r -p "-- Enter for step $step: $1 " _
  fi
  printf '\n==== %d. %s (%ds)\n' "$step" "$1" "$((SECONDS - started))"
}

section "start the stack"
if [ "$fresh" = 1 ]; then
  docker compose down -v --remove-orphans
fi
bash scripts/stack-up.sh
# The scenario helpers turn Git Bash path conversion off; stack-up runs before them.
source scripts/scenarios/lib.sh

section "initial snapshot: seeded employees as events, read model equal to HrCore"
bash scripts/scenarios/01-initial-snapshot.sh

section "a raise recorded today, effective three months ago (scenario 3)"
bash scripts/scenarios/03-retroactive-raise.sh

section "what it produced"
effective=$(sql_value "SELECT CONVERT(char(10), DATEADD(month, -3, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1)), 23);")
raise=$(events | grep -E $'^1:1003\t' | grep '"eventType":"SalaryChanged"' | grep '"isRetroactive":true' | tail -n 1 | cut -f 2)
echo "-- the business event on hr.employee-pay.v1 (key 1:1003):"
# Amounts are Avro decimals (bytes) and print unreadably here; the Query API below shows them.
echo "$raise" | sed 's/,"/,\n  "/g' | grep -v 'Amount"' | head -n 30
echo "  (amounts left out: Avro decimals; see the Query API below)"
recorded_ms=$(echo "$raise" | grep -oE '"recordedAt":[0-9]+' | grep -oE '[0-9]+')
effective_days=$(echo "$raise" | grep -oE '"effectiveFrom":[0-9]+' | grep -oE '[0-9]+')
echo "effectiveFrom $effective_days = $(date -u -d "@$((effective_days * 86400))" +%F) (Avro date: days since 1970-01-01);" \
  "recordedAt $recorded_ms = $(iso_millis "$recorded_ms") (the source commit)"
known_before=$(iso_millis $((recorded_ms - 1)))
echo
echo "-- its schema: subject hr.employee-pay.v1-value in the Schema Registry"
echo "versions: $(curl -fsS http://127.0.0.1:8081/subjects/hr.employee-pay.v1-value/versions)," \
  "compatibility: $(curl -fsS http://127.0.0.1:8081/config/hr.employee-pay.v1-value | grep -oE '[A-Z_]+"}' | tr -d '"}')"
echo "event types in the payload union: $(curl -fsS http://127.0.0.1:8081/subjects/hr.employee-pay.v1-value/versions/latest/schema \
  | grep -oE '"name":"[A-Za-z]+Payload"' | cut -d'"' -f4 | tr '\n' ' ')"
echo
echo "-- the read model through the Query API"
echo "GET /tenants/1/employees/1003/salary?validAt=$effective"
api "/tenants/1/employees/1003/salary?validAt=$effective"
echo
echo "GET /tenants/1/employees/1003/salary?validAt=$effective&knownAt=$known_before   (as known just before the raise)"
api "/tenants/1/employees/1003/salary?validAt=$effective&knownAt=$known_before"
echo
echo "GET /tenants/1/employees/1003/salary-history   (every version, with valid and recorded time; first 3)"
api "/tenants/1/employees/1003/salary-history" | sed 's/},{/},\n{/g' | head -n 3
echo

section "a failure: poison messages, the dead-letter topic, and the DlqNotEmpty alert (drill 7)"
SKIP_RESOLVE=1 bash scripts/drills/07-poison-messages.sh | grep -vE '^(Raise for|read model:)'
echo "-- the alert as Alertmanager has it:"
curl -fsS http://127.0.0.1:9093/api/v2/alerts | grep -oE '"alertname":"[A-Za-z]+"|"summary":"[^"]*"' | paste - - | head -n 5

section "where to look next"
cat <<'EOF'
Grafana, dashboard "CDC Pay Pipeline"   http://127.0.0.1:3000   (admin, GRAFANA_ADMIN_PASSWORD from .env)
Prometheus targets and alerts           http://127.0.0.1:9090/alerts
Alertmanager                            http://127.0.0.1:9093
Kafka UI: topics, messages, hr.dlq      http://127.0.0.1:8080
Query API                               http://127.0.0.1:5103/tenants/1/employees/1003/salary-history
More: docs/walkthrough.md, the other scenarios in scripts/scenarios/, the drills in scripts/drills/.
EOF
pass "walkthrough finished in $((SECONDS - started)) s"
