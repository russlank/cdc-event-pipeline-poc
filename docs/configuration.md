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
| 29092 | Kafka listener for host clients |
| 8081 | Schema Registry |
| 8083 | Kafka Connect REST |
| 8080 | Kafka UI |

## Service Settings

The .NET services read standard ASP.NET Core configuration (`appsettings.json`,
environment variables). Their settings are documented here as they are added.
