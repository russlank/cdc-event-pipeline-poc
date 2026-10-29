#!/bin/sh
# Contract compatibility gate, used by CI and runnable against any Schema Registry:
#   1. registers every contract version (contracts/avro/v1, v2, ...) in order under a
#      throwaway subject with FULL_TRANSITIVE; fails if any version breaks an earlier one;
#   2. checks that every schema in contracts/examples/breaking is rejected, proving the
#      gate rejects what it should;
#   3. deletes the throwaway subject.
# POSIX sh and curl only, so it runs in the curlimages/curl image.
#
# Usage: ci/schema-compat.sh <registry-url> [contracts-dir]

set -eu

registry=${1:?usage: schema-compat.sh <registry-url> [contracts-dir]}
contracts=${2:-$(dirname "$0")/../contracts}
scripts="$(dirname "$0")/../contracts/scripts"
subject="ci-compat-$(date +%s)-$$-value"

cleanup() {
  curl -s -o /dev/null -X DELETE "$registry/subjects/$subject" || true
  curl -s -o /dev/null -X DELETE "$registry/subjects/$subject?permanent=true" || true
}
trap cleanup EXIT

echo "== waiting for $registry"
attempt=0
until curl -fs -o /dev/null "$registry/subjects"; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 60 ] || { echo "Schema Registry not reachable at $registry" >&2; exit 2; }
  sleep 2
done

echo "== registering all contract versions under $subject"
if ! sh "$scripts/register-all.sh" "$registry" "$subject" "$contracts" FULL_TRANSITIVE; then
  echo "FAIL: the contract versions are not FULL_TRANSITIVE compatible (see above)" >&2
  exit 1
fi

for example in "$contracts"/examples/breaking/*.avsc; do
  [ -e "$example" ] || continue
  echo "== expecting rejection: $example"
  set +e
  sh "$scripts/check-compatibility.sh" "$registry" "$subject" "$example"
  result=$?
  set -e
  case "$result" in
    1) echo "rejected as expected" ;;
    0) echo "FAIL: breaking example was accepted: $example" >&2; exit 1 ;;
    *) echo "FAIL: compatibility check could not run" >&2; exit 2 ;;
  esac
done

echo "== contract compatibility gate passed"
