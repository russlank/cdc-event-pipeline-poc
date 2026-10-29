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

## Service Settings

The .NET services read standard ASP.NET Core configuration (`appsettings.json`,
then environment variables with `__` as the section separator). Required
settings are validated at startup.

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

The translator serves its health and identity endpoints on `127.0.0.1:5101`.
