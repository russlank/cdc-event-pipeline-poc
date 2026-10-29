#!/usr/bin/env bash
# Regenerates the C# contract classes from the Avro schemas with avrogen (pinned in
# dotnet-tools.json). Commit the result; a unit test fails when the generated schema and
# contracts/avro differ.
#
# Usage: contracts/scripts/generate-csharp.sh [version]   (default: v1)

set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
version=${1:-v1}
schema="$root/contracts/avro/$version/PayEvent.avsc"
target="$root/src/CdcPay.Contracts/Generated"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cd "$root"
dotnet tool restore >/dev/null
dotnet tool run avrogen -s "$schema" "$work" >/dev/null

mkdir -p "$target"
find "$target" -maxdepth 1 -name '*.cs' -delete
find "$work" -name '*.cs' -exec cp {} "$target/" \;
echo "Generated $(find "$target" -maxdepth 1 -name '*.cs' | wc -l) files in src/CdcPay.Contracts/Generated from $schema"
