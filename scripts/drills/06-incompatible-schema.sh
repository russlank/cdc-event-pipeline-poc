#!/usr/bin/env bash
# Drill 6: a schema change that breaks compatibility (contracts/examples/breaking: contract v2 with
# SalaryChangedPayload.monthlyAmount renamed to amount), tried on every path into the pipeline.
#   1. Registry compatibility check against the live subject.
#   2. Registration under the live subject, as a producer with auto-registration would do.
#   3. Schema lookup under the live subject, as the translator's serializer does (it never
#      registers): without an id it cannot write a message.
#   4. The CI gate with the breaking schema added as contract v3.
#   5. A throwaway copy of the subject with its compatibility lowered to NONE: is the registry the
#      last line?
# Expected: the registry default for new subjects is FULL_TRANSITIVE; 1 to 4 stop the schema
# (incompatible, HTTP 409, HTTP 404, gate exit 1); the live subject keeps its versions; the business
# topic and the read model are untouched; a raise still flows.
# Run on a caught-up stack while the source is otherwise quiet.

source "$(dirname "$0")/lib.sh"

registry=http://127.0.0.1:8081
subject=hr.employee-pay.v1-value
breaking=contracts/examples/breaking/PayEvent.avsc
throwaway="drill-incompatible-$(date +%s)-value"
contracts_copy=$(mktemp -d)

cleanup() {
  rm -rf "$contracts_copy"
  curl -s -o /dev/null -X DELETE "$registry/subjects/$throwaway" || true
  curl -s -o /dev/null -X DELETE "$registry/subjects/$throwaway?permanent=true" || true
}
trap cleanup EXIT

# schema_body <file>: the registry request body for an Avro schema file.
schema_body() {
  printf '{"schemaType":"AVRO","schema":"%s"}' "$(tr -d '\r\n' < "$1" | sed 's/"/\\"/g')"
}

# registry_call <method> <path> [file]: prints "<HTTP status> <body>" for a registry request.
registry_call() {
  local body out
  body=${3:+$(schema_body "$3")}
  out=$(curl -s -w '\n%{http_code}' -X "$1" -H 'Content-Type: application/vnd.schemaregistry.v1+json' \
    ${body:+--data "$body"} "$registry$2")
  printf '%s %s\n' "$(printf '%s\n' "$out" | tail -n 1)" "$(printf '%s\n' "$out" | sed '$d' | head -c 300)"
}

# contract_script <script> <args...>: runs a contract or CI script with Git Bash path conversion on
# (the scripts keep curl output in /tmp; the scenario helpers turn conversion off).
contract_script() {
  env -u MSYS_NO_PATHCONV sh "$@"
}

versions() { curl -fsS "$registry/subjects/$subject/versions"; }

echo "== before"
wait_for 120 "the projector to catch up" group_caught_up "$projector"
divergence_before=$(readmodel_divergence)
events_before=$(processed_events)
business_before=$(end_offset hr.employee-pay.v1)
versions_before=$(versions)
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "subject $subject: versions $versions_before, compatibility $(curl -fsS "$registry/config/$subject")"
default_level=$(curl -fsS "$registry/config")
echo "registry default for new subjects: $default_level"
echo "$default_level" | grep -q FULL_TRANSITIVE || fail "a new subject would get $default_level, which does not protect old readers"

echo "== 1. compatibility check against the latest version"
check=$(registry_call POST "/compatibility/subjects/$subject/versions/latest?verbose=true" "$breaking")
echo "$check"
echo "$check" | grep -q '"is_compatible":false' || fail "the registry did not find the schema incompatible"

echo "== 2. register it, as an auto-registering producer would"
register=$(registry_call POST "/subjects/$subject/versions" "$breaking")
echo "$register"
[ "${register%% *}" = 409 ] || fail "registration was not rejected with HTTP 409"

echo "== 3. look it up, as the translator's serializer does (auto-registration off)"
lookup=$(registry_call POST "/subjects/$subject" "$breaking")
echo "$lookup"
[ "${lookup%% *}" = 404 ] || fail "the lookup found an id for the breaking schema"

echo "== 4. CI gate with the breaking schema as contract v3"
cp -r contracts/avro contracts/examples "$contracts_copy/"
mkdir -p "$contracts_copy/avro/v3"
cp "$breaking" "$contracts_copy/avro/v3/PayEvent.avsc"
if gate=$(contract_script ci/schema-compat.sh "$registry" "$contracts_copy" 2>&1); then
  fail "the CI gate passed with the breaking schema"
fi
echo "$gate" | grep -E 'FAIL|HTTP 409' | head -n 3

echo "== 5. throwaway subject with v1 and v2, compatibility lowered to NONE"
contract_script contracts/scripts/register-all.sh "$registry" "$throwaway" contracts FULL_TRANSITIVE >/dev/null
echo "under FULL_TRANSITIVE: $(registry_call POST "/subjects/$throwaway/versions" "$breaking" | cut -c 1-80)"
curl -fsS -X PUT -H 'Content-Type: application/vnd.schemaregistry.v1+json' \
  --data '{"compatibility":"NONE"}' "$registry/config/$throwaway" >/dev/null
lowered=$(registry_call POST "/subjects/$throwaway/versions" "$breaking")
echo "under NONE: $lowered"

echo "== after: the pipeline still flows"
raises 3001 3001
raise_applied() { [ "$(processed_events)" -gt "$events_before" ]; }
wait_for 120 "the raise to reach the read model" raise_applied
wait_for 120 "the read model to match the source" readmodel_back_to "$divergence_before"
echo "subject $subject: versions $(versions); business topic +$(($(end_offset hr.employee-pay.v1) - business_before)) (the raise)"
echo "translator: $(docker compose ps event-translator --format '{{.Status}}')"
print_translator_losses "$started"

[ "$(versions)" = "$versions_before" ] || fail "the live subject's versions changed"
if [ "${lowered%% *}" = 200 ]; then
  echo "note: with compatibility NONE the registry accepts the breaking schema; only its configuration and the CI gate guard a subject"
fi
pass "the breaking schema was stopped by the registry (check, 409 on register, 404 on lookup) and by the CI gate; the live subject and the read model are unchanged"
