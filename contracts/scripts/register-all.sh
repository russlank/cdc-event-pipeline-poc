#!/bin/sh
# Registers every contract version (contracts/avro/v1, v2, ...) in order under one subject.
# Under FULL_TRANSITIVE each version must be compatible with all earlier ones, so an
# incompatible version fails here with HTTP 409 and the script exits non-zero.
# Idempotent: versions already registered return their existing ids.
#
# Usage: register-all.sh <registry-url> <subject> [contracts-dir] [compatibility]
#   contracts-dir defaults to the contracts/ folder next to this script.

set -eu

registry=${1:?usage: register-all.sh <registry-url> <subject> [contracts-dir] [compatibility]}
subject=${2:?usage: register-all.sh <registry-url> <subject> [contracts-dir] [compatibility]}
contracts=${3:-$(dirname "$0")/..}
compatibility=${4:-FULL_TRANSITIVE}

versions=$(ls -d "$contracts"/avro/v* | sed 's#.*/v##' | sort -n)
[ -n "$versions" ] || { echo "no contract versions under $contracts/avro" >&2; exit 2; }

for version in $versions; do
  echo "== contract v$version"
  sh "$(dirname "$0")/register-schema.sh" "$registry" "$subject" "$contracts/avro/v$version/PayEvent.avsc" "$compatibility"
done
