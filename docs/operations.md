# Operations

Document id: `operations`
Status: draft
Last updated: 2026-10-29

## Purpose

Commands for running, inspecting, and resetting the stack. Replay, backfill,
and schema migration runbooks are added here as they are built.

## Start, Stop, Reset

```sh
scripts/stack-up.sh                # start everything; wait for init jobs and health checks
docker compose stop connect        # stop one service (state is kept)
docker compose start connect
docker compose down                # remove all containers; all data is lost
```

The one-shot jobs (`db-init`, `kafka-init`, `connect-init`) are idempotent and
can be rerun with `docker compose up <job>`. Plain `docker compose up --wait`
reports a failure when a job exits, even successfully; use the script.

On Windows Git Bash, run `export MSYS_NO_PATHCONV=1` first, or Git Bash
rewrites container paths such as `/opt/kafka/bin/...` into Windows paths.

## Inspect

```sh
docker compose ps
docker compose logs -f connect
curl -s http://localhost:8083/connectors?expand=status
docker compose exec kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka:9092 --list
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server kafka:9092 --topic cdc.HrCore.dbo.Salary --from-beginning --property print.key=true
```

## Source Database

`db-init` runs `db/schema.sql`, `db/seed.sql`, and `db/enable-cdc.sql`:
the `HrCore` schema, deterministic synthetic data (3 tenants, 50 employees),
and CDC on every table. CDC capture and cleanup run as SQL Server Agent jobs
(default retention: 3 days).

```sh
docker compose exec sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d HrCore -Q "EXEC sys.sp_cdc_help_jobs"'
```

`scripts/checks/raw-cdc-smoke.sql` makes a multi-row transaction, an update,
an insert, and a delete, to check that all of them reach the `cdc.*` topics.

## Debezium Connector

`connect-init` registers `connect/hrcore-connector.json` through
`connect/register-connector.sh` (idempotent `PUT`, then waits for `RUNNING`).
Settings that matter:

| Setting | Why |
|---|---|
| `snapshot.isolation.mode=snapshot` | consistent initial snapshot without shared locks; the default kept locks that blocked all updates |
| `provide.transaction.metadata=true` | `BEGIN`/`END` per source transaction on `cdc.transaction`, with event counts per table |
| `signal.data.collection` | incremental snapshots on demand through `dbo.DebeziumSignal` |
| `heartbeat.action.query` | updates `dbo.DebeziumHeartbeat` every 10 s, so the source always changes while the connector runs |
| `decimal.handling.mode=string` | amounts arrive as exact decimal strings |
| `tombstones.on.delete=false` | deletes are one event with the before image |

## Topics

`kafka-init` creates `hr.employee-pay.v1` (3 partitions) and `hr.dlq`
(1 partition), both with unlimited retention so the read model can always be
rebuilt. Debezium creates the `cdc.*` topics. Broker auto-creation is off.
