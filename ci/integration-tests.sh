#!/usr/bin/env bash
# Integration tests: the projector and read model against Kafka, Schema Registry, and PostgreSQL.
# With CDCPAY_IT_KAFKA, CDCPAY_IT_SCHEMA_REGISTRY, and CDCPAY_IT_POSTGRES set, the tests use
# those services (CI service containers); otherwise they start their own with Testcontainers,
# which needs a Docker daemon.
#
# Usage: ci/integration-tests.sh

set -euo pipefail

cd "$(dirname "$0")/../src"

dotnet test --project CdcPay.IntegrationTests/CdcPay.IntegrationTests.csproj -c Release
