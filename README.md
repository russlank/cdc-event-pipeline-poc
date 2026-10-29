# CDC Pay Events PoC

Proof of concept: SQL Server change data capture → Debezium → Kafka → versioned,
effective-dated business events (`EmployeeHired`, `SalaryChanged`,
`SalaryCorrected`, `PositionChanged`, `EmployeeTerminated`) with
schema-registry compatibility rules, a bitemporal read model with as-of
queries, replay, idempotency, and monitoring. Synthetic HR/pay data only.
**Not production.**

## Components

| Component | Technology |
|---|---|
| Source database `HrCore` | SQL Server 2022 with CDC and SQL Server Agent |
| Broker | Apache Kafka, single node, KRaft |
| CDC | Kafka Connect + Debezium SQL Server connector |
| Contracts | Avro schemas in Confluent Schema Registry |
| Event Translator | .NET 10 worker: raw CDC → business events |
| Pay History Projector | .NET 10 worker: business events → bitemporal read model in PostgreSQL |
| Pay Query API | ASP.NET Core minimal API: as-of salary queries |
| Monitoring | Prometheus, Grafana, Alertmanager |

## Quick Start

```sh
cp .env.example .env          # then set MSSQL_SA_PASSWORD
scripts/stack-up.sh          # SQL Server with CDC, Kafka, Debezium, Schema Registry, Kafka UI, translator
bash scripts/scenarios/05-hire-one-transaction.sh   # a hire becomes one EmployeeHired event
```

Details: [docs/getting-started.md](docs/getting-started.md).

## Documentation

| Document | Content |
|---|---|
| [docs/architecture.md](docs/architecture.md) | components, data flow, design decisions |
| [docs/event-design.md](docs/event-design.md) | topics, events, envelope, compatibility |
| [docs/configuration.md](docs/configuration.md) | secrets, ports, service settings |
| [docs/operations.md](docs/operations.md) | start, stop, inspect, reset |
| [docs/testing.md](docs/testing.md) | test projects and gates |
| [docs/journal.md](docs/journal.md) | what broke and how it was fixed |

## Build and Test

```sh
bash ci/dotnet-gates.sh       # restore, build, test, format: what CI runs
```

Or step by step:

```sh
cd src
dotnet restore ./CdcPay.slnx
dotnet build ./CdcPay.slnx -c Release --no-restore
dotnet test ./CdcPay.slnx -c Release --no-build
dotnet format ./CdcPay.slnx --verify-no-changes
```

Each service reports its version, commit, and branch at
`/system/component-identity` and in its first log line.

## Versions

Pinned on 2026-10-29. Images are taken a few weeks after release where a
newer one existed (for example Debezium 3.7.0 was one day old).

| Component | Version |
|---|---|
| SQL Server | `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` |
| Kafka | `apache/kafka:4.3.2` |
| Debezium Connect | `quay.io/debezium/connect:3.6.2.Final` (Kafka Connect 4.3.0) |
| Schema Registry | `confluentinc/cp-schema-registry:8.3.2` |
| Kafka UI | `ghcr.io/kafbat/kafka-ui:v1.5.0` |
| curl (init job) | `curlimages/curl:8.22.0` |
| .NET SDK and runtime | 10.0.402; images `mcr.microsoft.com/dotnet/sdk:10.0.402-noble`, `aspnet:10.0.14-noble` |
| avrogen | `Apache.Avro.Tools` 1.12.2 (`dotnet-tools.json`) |
| NuGet packages | `src/Directory.Packages.props` |
| CI | Woodpecker: `mcr.microsoft.com/dotnet/sdk:10.0.402-noble`; GitHub Actions: `actions/checkout@v7.0.1`, `actions/setup-dotnet@v6.0.0` |

Version of this repository: `0.1.0` (`src/Directory.Build.props`); tags are
`v<version>`.

## License

MIT, see [LICENSE](LICENSE).
