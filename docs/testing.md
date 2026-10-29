# Testing

Document id: `testing`
Status: draft
Last updated: 2026-10-29

## Purpose

How the code is tested and how to run the checks.

## Gates

Run from `src/`. CI (`.github/workflows/ci.yml`) runs the same commands.

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
| `CdcPay.UnitTests` | pure logic and in-memory host tests (`WebApplicationFactory`) |

Integration tests against Kafka, Schema Registry, and PostgreSQL
(Testcontainers) join with the projector.

## Conventions

- Test names: `Method_State_ExpectedBehavior` or `Scenario_WhenCondition_Result`.
- Synthetic data only; no real names or personal data in fixtures.
