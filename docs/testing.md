# Testing

Document id: `testing`
Status: draft
Last updated: 2026-10-29

## Purpose

How the code is tested and how to run the checks.

## Gates

`ci/dotnet-gates.sh` runs all four gates; both CI pipelines call it
(`.woodpecker.yml` in `mcr.microsoft.com/dotnet/sdk:10.0.402-noble`, and
`.github/workflows/ci.yml` on GitHub-compatible runners). Locally:

```sh
bash ci/dotnet-gates.sh            # optional: <branch> <repository-url> for the build identity
```

The same gates by hand, from `src/`:

```sh
dotnet restore ./CdcPay.slnx
dotnet build ./CdcPay.slnx -c Release --no-restore
dotnet test ./CdcPay.slnx -c Release --no-build
dotnet format ./CdcPay.slnx --verify-no-changes
```

The build treats warnings as errors and enforces code style, so a clean build
also means `dotnet format` finds nothing. Tests run on Microsoft.Testing.Platform
(xunit.v3), selected in `src/global.json`.

## Test Projects

| Project | Scope |
|---|---|
| `CdcPay.UnitTests` | pure logic and in-memory host tests (`WebApplicationFactory`): identity, event ids, Debezium parsing (fixtures recorded from the real connector in `Translation/Fixtures`), transaction assembly, offsets, every classification rule, Avro round trips, contract drift |

Integration tests against Kafka, Schema Registry, and PostgreSQL
(Testcontainers) join with the projector. End-to-end checks against the
running stack are the scenario scripts in `scripts/scenarios/`.

## Conventions

- Test names: `Method_State_ExpectedBehavior` or `Scenario_WhenCondition_Result`.
- Synthetic data only; no real names or personal data in fixtures.
