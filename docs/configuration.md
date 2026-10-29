# Configuration

Document id: `configuration`
Status: draft
Last updated: 2026-10-29

## Purpose

Every setting the stack and the services read, and where it comes from.

## Secrets

Secrets live in `.env` at the repository root (git-ignored); `.env.example`
lists them. Compose passes them to the containers that need them.

| Variable | Used by | Notes |
|---|---|---|
| `MSSQL_SA_PASSWORD` | `sqlserver`, `db-init`, `connect` | SQL Server password policy: at least 8 characters with upper, lower, digit, and symbol |
| `POSTGRES_PASSWORD` | `postgres`, `pay-history-projector` | the read model's owner login `payhistory` |
| `PAYQUERY_DB_PASSWORD` | `postgres`, `pay-query-api` | the read-only login `pay_query`, created by `db/readmodel/init/create-query-role.sh` |
| `GRAFANA_ADMIN_PASSWORD` | `grafana` | login `admin` |
| `ALERTMANAGER_CONFIG` | `alertmanager` | not a secret: `alertmanager.yml` (default) or `alertmanager-slack.yml`; the Slack webhook URL itself goes in `monitoring/alertmanager/slack-api-url` (git-ignored) |

The PostgreSQL passwords go into connection strings, so they must not contain
`;`.

The Debezium connector configuration references the password as
`${env:MSSQL_SA_PASSWORD}` (Kafka's `EnvVarConfigProvider`), so it is never
written to the connector JSON or to Kafka.

## Ports

All ports bind to `127.0.0.1`.

| Port | Service |
|---|---|
| 1433 | SQL Server |
| 29092 | Kafka listener for host clients (use `127.0.0.1`, not `localhost`) |
| 8081 | Schema Registry |
| 8083 | Kafka Connect REST |
| 8080 | Kafka UI |
| 5432 | PostgreSQL (read model) |
| 5101 | Event Translator (health, identity) |
| 5102 | Pay History Projector (health, identity) |
| 5103 | Pay Query API |
| 8889 | OpenTelemetry Collector: metrics of the .NET services (`/metrics`) |
| 9090 | Prometheus |
| 9093 | Alertmanager |
| 3000 | Grafana |

## Service Settings

The .NET services read standard ASP.NET Core configuration (`appsettings.json`,
then environment variables with `__` as the section separator). Required
settings are validated at startup.

Telemetry and logging, the same for every service (compose sets them through
`x-dotnet-telemetry`):

| Setting | Compose value | Notes |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://otel-collector:4317` | unset: no metric or trace export |
| `OTEL_METRIC_EXPORT_INTERVAL` | `10000` | milliseconds between metric pushes |
| `Logging:Console:FormatterName` | `json` | one JSON record per line; default is the plain console format |
| `Logging:Console:FormatterOptions:IncludeScopes` | `true` | adds `TraceId` and `SpanId` while a span is active |
| `Logging:Console:FormatterOptions:UseUtcTimestamp`, `TimestampFormat` | `true`, `yyyy-MM-ddTHH:mm:ss.fffZ` | |

### Event Translator

| Setting | Default | Notes |
|---|---|---|
| `Kafka:BootstrapServers` | `127.0.0.1:29092` | compose: `kafka:9092` |
| `Kafka:SchemaRegistryUrl` | `http://127.0.0.1:8081` | compose: `http://schema-registry:8081` |
| `Kafka:ConsumerGroup` | `cdcpay-event-translator` | |
| `Kafka:RawTopicPattern` | `^cdc\.HrCore\.dbo\..*` | regex subscription |
| `Kafka:TransactionTopic` | `cdc.transaction` | |
| `Kafka:CommitInterval` | `00:00:01` | |
| `Translation:TransactionTimeout` | `00:01:00` | incomplete transactions are dead-lettered after this |
| `Translation:SupportedCurrencies` | `SEK, EUR, NOK, DKK, USD` | other currencies are dead-lettered |

### Pay History Projector

| Setting | Default | Notes |
|---|---|---|
| `Kafka:BootstrapServers` | `127.0.0.1:29092` | compose: `kafka:9092` |
| `Kafka:SchemaRegistryUrl` | `http://127.0.0.1:8081` | compose: `http://schema-registry:8081` |
| `Kafka:ConsumerGroup` | `cdcpay-pay-history-projector` | |
| `Kafka:Topic` | `hr.employee-pay.v1` | |
| `Kafka:DeadLetterTopic` | `hr.dlq` | for messages that are not usable business events |
| `Kafka:CommitInterval` | `00:00:01` | offsets of applied events are committed this often |
| `Kafka:RetryDelay` | `00:00:02` | wait between attempts while PostgreSQL or the Schema Registry is unreachable |
| `ConnectionStrings:ReadModel` | none, required | Npgsql connection string of the owner login |

### Pay Query API

| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:ReadModel` | none, required | Npgsql connection string of the read-only login |
