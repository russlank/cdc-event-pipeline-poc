#!/usr/bin/env bash
# The .NET quality gates, shared by every CI pipeline and runnable locally:
# restore, Release build (warnings and style are errors), unit tests, format check.
# Integration tests need Kafka, Schema Registry, and PostgreSQL: ci/integration-tests.sh.
#
# Usage: ci/dotnet-gates.sh [branch] [repository-url]
#   branch and repository URL are embedded in the build identity when given.

set -euo pipefail

cd "$(dirname "$0")/../src"

branch=${1:-}
repository_url=${2:-}

build_props=(-p:ContinuousIntegrationBuild=true)
[ -n "$branch" ] && build_props+=("-p:SourceBranch=$branch")
[ -n "$repository_url" ] && build_props+=("-p:RepositoryUrl=$repository_url")

step() { printf '\n== %s\n' "$1"; }

step "restore"
dotnet restore ./CdcPay.slnx

step "build"
dotnet build ./CdcPay.slnx -c Release --no-restore "${build_props[@]}"

step "unit tests"
dotnet test --project CdcPay.UnitTests/CdcPay.UnitTests.csproj -c Release --no-build

step "format"
dotnet format ./CdcPay.slnx --verify-no-changes

step "all gates passed"
