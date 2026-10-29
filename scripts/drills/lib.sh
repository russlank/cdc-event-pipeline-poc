# Helpers for the failure drills, on top of the scenario helpers. Source from a drill script:
#   source "$(dirname "$0")/lib.sh"
# shellcheck shell=bash

source "$(dirname "${BASH_SOURCE[0]}")/../scenarios/lib.sh"

translator=cdcpay-event-translator
projector=cdcpay-pay-history-projector

# raises <first> <last>: commits a raise for each employee <first>..<last> of tenant 3.
raises() {
  run_sql <(printf ':setvar First %s\n:setvar Last %s\n' "$1" "$2"; cat scripts/drills/raises.sql)
}

# task_state: prints the state of the connector's task (RUNNING, FAILED, ...), nothing while
# Connect does not answer.
task_state() {
  curl -fsS localhost:8083/connectors/hrcore-cdc/status 2>/dev/null \
    | sed -n 's/.*"tasks":\[{"id":0,"state":"\([A-Z]*\)".*/\1/p' || true
}

task_running() { [ "$(task_state)" = RUNNING ]; }

# processed_events: prints the number of events the projector has applied.
processed_events() {
  readmodel_sql 'SELECT count(*) FROM processed_event'
}

# alert_logged <FIRING|RESOLVED> <alert> <since>: succeeds when alert-sink has logged the alert in
# that state since <since> (ISO 8601 with a zone).
alert_logged() {
  docker compose logs --since "$3" --no-log-prefix alert-sink 2>&1 | grep -qE "^ALERT $1 $2 "
}

# print_alerts <since>: prints every alert alert-sink logged since <since>.
print_alerts() {
  docker compose logs --since "$1" --no-log-prefix alert-sink 2>&1 | grep -E '^ALERT ' | cut -c 1-160 || true
}

# print_translator_losses <since>: prints the translator's abandoned and dead-lettered messages.
print_translator_losses() {
  docker compose logs --since "$1" --no-log-prefix event-translator 2>/dev/null \
    | grep -oE '"Message":"(Abandoned|Dead-lettered)[^"]*"' | cut -c 1-160 || true
}

# readmodel_back_to <divergence>: succeeds when the projector has caught up and the read model
# differs from the source exactly as given (as before the drill). The translator's group is not
# waited for: its offset on the heartbeat topic stays one behind while the latest heartbeat
# transaction waits for its END.
readmodel_back_to() {
  group_caught_up "$projector" && [ "$(readmodel_divergence)" = "$1" ]
}

# require_drill_rows_in_sync: fails when rows of employees 3001..3005 already differ from the
# source; the drills compare the read model before and after, which needs them in sync.
require_drill_rows_in_sync() {
  if echo "$1" | grep -qE '^[<>] 3\|300[1-5]\|'; then
    fail "rows of employees 3001..3005 differ from the source before the drill; repair with scripts/ops/backfill.sh Salary"
  fi
}
