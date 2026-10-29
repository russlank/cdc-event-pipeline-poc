# Getting Started

Document id: `getting-started`
Status: active
Last updated: 2026-10-29

## Purpose

Run the stack locally and check that it works.

## Inputs

- Docker Desktop (or Docker Engine) with Compose v2, about 8 GB of memory
  for the stack.
- .NET SDK 10.0.402 or a later 10.0 feature band (see `src/global.json`).
- Bash (Git Bash on Windows) for the scripts.

## Steps

1. Create the local secrets file:

   ```sh
   cp .env.example .env
   # edit .env: set a strong MSSQL_SA_PASSWORD and the two PostgreSQL passwords
   ```

2. Start the stack and wait until the init jobs are done and every service
   is healthy:

   ```sh
   scripts/stack-up.sh
   ```

3. Check the services:

   ```sh
   docker compose ps
   curl -s 'http://localhost:8083/connectors?expand=status'  # connector hrcore-cdc RUNNING
   curl -s http://localhost:8081/subjects                  # Schema Registry
   ```

   Kafka UI is at <http://localhost:8080>.

4. Run the demo scenarios, then ask the Query API:

   ```sh
   for s in scripts/scenarios/0*.sh; do bash "$s"; done
   curl -s 'http://127.0.0.1:5103/tenants/1/employees/1003/salary-history'
   ```

5. Build and test the .NET solution:

   ```sh
   bash ci/dotnet-gates.sh          # restore, build, unit tests, format
   bash ci/integration-tests.sh     # Kafka, Schema Registry, PostgreSQL in Testcontainers
   ```

## Outputs

| Service | Address |
|---|---|
| SQL Server | `localhost,1433` (user `sa`, password from `.env`) |
| Kafka (from the host) | `127.0.0.1:29092` |
| Kafka Connect REST | <http://localhost:8083> |
| Schema Registry | <http://localhost:8081> |
| Kafka UI | <http://localhost:8080> |
| PostgreSQL read model | `127.0.0.1:5432`, database `payhistory` (logins `payhistory`, `pay_query`) |
| Pay Query API | <http://127.0.0.1:5103> |

To start from scratch: `docker compose down` (the stack keeps no volumes).
