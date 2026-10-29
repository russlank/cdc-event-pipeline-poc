# Testing

Document id: `testing`
Status: draft
Last updated: 2026-10-29

## Purpose

How the code is tested and how to run the checks.

## Gates

Three gate scripts; both CI pipelines (`.woodpecker.yml` and
`.github/workflows/ci.yml`) run all three:

| Script | Gate |
|---|---|
| `ci/dotnet-gates.sh` | restore, build, unit tests, format |
| `ci/integration-tests.sh` | integration tests against Kafka, Schema Registry, and PostgreSQL |
| `ci/schema-compat.sh` | contract compatibility |

Locally:

```sh
bash ci/dotnet-gates.sh            # optional: <branch> <repository-url> for the build identity
bash ci/integration-tests.sh       # needs Docker (Testcontainers)
sh ci/schema-compat.sh http://127.0.0.1:8081
```

The same gates by hand, from `src/`:

```sh
dotnet restore ./CdcPay.slnx
dotnet build ./CdcPay.slnx -c Release --no-restore
dotnet test --project CdcPay.UnitTests/CdcPay.UnitTests.csproj -c Release --no-build
dotnet format ./CdcPay.slnx --verify-no-changes
```

The build treats warnings as errors and enforces code style, so a clean build
also means `dotnet format` finds nothing. Tests run on Microsoft.Testing.Platform
(xunit.v3), selected in `src/global.json`.

## Test Projects

| Project | Scope |
|---|---|
| `CdcPay.UnitTests` | pure logic and in-memory host tests (`WebApplicationFactory`): identity, event ids, Debezium parsing (fixtures recorded from the real connector in `Translation/Fixtures`), transaction assembly including redelivery, offsets, every classification rule, Avro round trips, contract drift, v1/v2 resolution in both directions, every case of the bitemporal merge, event-to-fact mapping, as-of parameter parsing, each Query API endpoint against an in-memory reader, the projector's failure classification and counters, the dead-letter headers, the rule that logs and dead-letter errors name codes but never row content, the telemetry catalog (translator instruments, unique log event ids per catalog range, bounded metric tags and span attributes, trace continuation through the `traceparent` header, probes out of traces and request logs) |
| `CdcPay.IntegrationTests` | the projector host against real Kafka, Schema Registry, and PostgreSQL: a fixture stream (with a redelivered event) becomes the expected history, offsets are committed to the end, a replay with a new consumer group changes nothing; a non-Avro message and a mismatched payload go to the dead-letter topic with their headers while projection continues; the schema applies twice; the no-overlap constraint holds |

The integration tests start their own containers with Testcontainers. When
`CDCPAY_IT_KAFKA`, `CDCPAY_IT_SCHEMA_REGISTRY`, and `CDCPAY_IT_POSTGRES` (an
Npgsql connection string with the right to create databases) are all set, they
use those services instead; Woodpecker does this with service containers.
Every test gets its own topic, subject, and database.

End-to-end checks against the running stack are the scenario scripts in
`scripts/scenarios/`: 01 snapshot and read model, 02 raise next month, 03
retroactive raise with as-of queries, 04 correction, 05 hire in one
transaction, 06 business and raw replay (idempotency), 07 dead letters.
Monitoring is checked against the running stack: every Prometheus target is
`up`, every dashboard query returns data, and each alert fires and resolves
when its cause is produced and removed ([monitoring.md](monitoring.md)).
Operations have their own checks: `scripts/ops/replay-projector.sh` compares
the read-model checksum before and after a rebuild, and
`scripts/checks/backfill-while-streaming.sh` runs a backfill with a
concurrent change.

## Conventions

- Test names: `Method_State_ExpectedBehavior` or `Scenario_WhenCondition_Result`.
- Synthetic data only; no real names or personal data in fixtures.
