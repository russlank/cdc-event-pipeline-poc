#!/usr/bin/env bash
# Starts the local stack and waits until it is ready.
# `docker compose up --wait` alone fails as soon as a one-shot init job exits, even with
# exit code 0, so this script waits for each job's exit code, then for the health checks
# of the long-running services.
#
# Usage: scripts/stack-up.sh

set -euo pipefail

cd "$(dirname "$0")/.."

jobs=(db-init kafka-init connect-init schemas-init)
services=(sqlserver kafka schema-registry connect kafka-ui postgres event-translator pay-history-projector pay-query-api
  otel-collector kafka-exporter prometheus alertmanager alert-sink grafana)

# Build identity for the .NET images.
SOURCE_REVISION_ID=$(git rev-parse HEAD 2>/dev/null || true)
SOURCE_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || true)
REPOSITORY_URL=$(git remote get-url origin-public 2>/dev/null || git remote get-url origin 2>/dev/null || true)
export SOURCE_REVISION_ID SOURCE_BRANCH REPOSITORY_URL

docker compose --progress quiet up -d --build

for job in "${jobs[@]}"; do
  container=$(docker compose ps -aq "$job")
  code=$(docker wait "$container")
  if [ "$code" != "0" ]; then
    echo "$job failed with exit code $code:" >&2
    docker compose logs "$job" >&2
    exit 1
  fi
  echo "$job completed"
done

docker compose --progress quiet up -d --wait "${services[@]}"
docker compose ps
