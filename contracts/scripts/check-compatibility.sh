#!/bin/sh
# Asks the registry whether a schema is compatible with every version of a subject,
# under the subject's compatibility mode. Does not register anything.
#
# Usage: check-compatibility.sh <registry-url> <subject> <schema.avsc>
# Exit codes: 0 compatible, 1 incompatible, 2 request failed.

set -eu

registry=${1:?usage: check-compatibility.sh <registry-url> <subject> <schema.avsc>}
subject=${2:?usage: check-compatibility.sh <registry-url> <subject> <schema.avsc>}
file=${3:?usage: check-compatibility.sh <registry-url> <subject> <schema.avsc>}

# See register-schema.sh: the .avsc files contain no backslashes or tabs.
escaped=$(tr -d '\r\n' < "$file" | sed 's/"/\\"/g')
response=$(curl -s -w '\n%{http_code}' -X POST \
  -H 'Content-Type: application/vnd.schemaregistry.v1+json' \
  --data "{\"schemaType\":\"AVRO\",\"schema\":\"$escaped\"}" \
  "$registry/compatibility/subjects/$subject/versions?verbose=true")
status=$(printf '%s' "$response" | tail -n 1)
body=$(printf '%s' "$response" | sed '$d')

case "$status" in
  2*) ;;
  *) echo "HTTP $status: $body" >&2; exit 2 ;;
esac

if printf '%s' "$body" | grep -q '"is_compatible":true'; then
  echo "compatible: $file"
  exit 0
fi

echo "incompatible: $file"
printf '%s\n' "$body" | sed 's/","/"\n  "/g'
exit 1
