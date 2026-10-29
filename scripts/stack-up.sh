#!/usr/bin/env bash
# Starts the local stack and waits until it is ready.
# `docker compose up --wait` alone fails as soon as a one-shot init job exits, even with
# exit code 0, so this script waits for each job's exit code, then for the health checks
# of the long-running services.
#
# Usage: scripts/stack-up.sh

set -euo pipefail

cd "$(dirname "$0")/.."

jobs=(db-init kafka-init connect-init)
services=(sqlserver kafka schema-registry connect kafka-ui)

docker compose up -d

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

docker compose up -d --wait "${services[@]}"
docker compose ps
