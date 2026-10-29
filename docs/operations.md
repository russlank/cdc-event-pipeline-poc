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

## Event Translator and Scenarios

```sh
curl -s http://127.0.0.1:5101/health/ready
curl -s http://127.0.0.1:5101/system/component-identity
bash scripts/scenarios/01-initial-snapshot.sh      # every seeded employee as EmployeeHired
bash scripts/scenarios/02-raise-next-month.sh      # SalaryChanged, not retroactive
bash scripts/scenarios/05-hire-one-transaction.sh  # one EmployeeHired from three tables
```

Each scenario runs its `.sql` file (safe to rerun) and checks the events.
Business events decoded through the registry:

```sh
docker compose exec schema-registry kafka-avro-console-consumer \
  --bootstrap-server kafka:9092 --topic hr.employee-pay.v1 --from-beginning \
  --property schema.registry.url=http://localhost:8081 --property print.key=true \
  --key-deserializer org.apache.kafka.common.serialization.StringDeserializer
```

Dead letters with their headers:

```sh
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server kafka:9092 \
  --topic hr.dlq --from-beginning --formatter-property print.headers=true
```

## Contracts

`schemas-init` runs `contracts/scripts/register-schema.sh`: it sets
`FULL_TRANSITIVE` on `hr.employee-pay.v1-value` and registers
`contracts/avro/v1/PayEvent.avsc`. After changing the schema, regenerate the C#
classes with `bash contracts/scripts/generate-csharp.sh`.

## Topics

`kafka-init` creates `hr.employee-pay.v1` (3 partitions) and `hr.dlq`
(1 partition), both with unlimited retention so the read model can always be
rebuilt. Debezium creates the `cdc.*` topics. Broker auto-creation is off.
