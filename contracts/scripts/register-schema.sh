#!/bin/sh
# Sets a subject's compatibility mode and registers an Avro schema under it.
# Producers never auto-register; this script (and CI) is the only way a schema version is
# added, so an incompatible schema is stopped here (HTTP 409).
# Idempotent: registering an identical schema returns the existing id.
#
# Usage: register-schema.sh <registry-url> <subject> <schema.avsc> [compatibility]
#   compatibility defaults to FULL_TRANSITIVE.
# Runs in the curlimages/curl container (POSIX sh, curl, tr, sed) or any shell with curl.

set -eu

registry=${1:?usage: register-schema.sh <registry-url> <subject> <schema.avsc> [compatibility]}
subject=${2:?usage: register-schema.sh <registry-url> <subject> <schema.avsc> [compatibility]}
file=${3:?usage: register-schema.sh <registry-url> <subject> <schema.avsc> [compatibility]}
compatibility=${4:-FULL_TRANSITIVE}

request() {
  # request <method> <path> <body>; prints the body, fails unless HTTP 2xx
  status=$(curl -s -o /tmp/registry-response.json -w '%{http_code}' -X "$1" \
    -H 'Content-Type: application/vnd.schemaregistry.v1+json' --data "$3" "$registry$2")
  head -c 200 /tmp/registry-response.json
  echo
  case "$status" in
    2*) return 0 ;;
    *) echo "HTTP $status from $1 $2" >&2; return 1 ;;
  esac
}

echo "Compatibility of $subject: $compatibility"
request PUT "/config/$subject" "{\"compatibility\":\"$compatibility\"}"

# The schema travels as a JSON string: join lines and escape quotes. The .avsc files in
# this repository contain no backslashes or tabs.
escaped=$(tr -d '\r\n' < "$file" | sed 's/"/\\"/g')

echo "Registering $file under $subject"
request POST "/subjects/$subject/versions" "{\"schemaType\":\"AVRO\",\"schema\":\"$escaped\"}"

request GET "/subjects/$subject/versions" ""
