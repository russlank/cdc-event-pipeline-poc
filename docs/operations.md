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

## Monitoring

Dashboard, metrics, alert rules, and what to do when an alert fires:
[monitoring.md](monitoring.md).

```sh
docker compose logs -f alert-sink     # every alert Alertmanager sends, firing and resolved
curl -s localhost:9090/api/v1/alerts  # alerts as Prometheus evaluates them
```

## Inspect

```sh
docker compose ps
docker compose logs -f connect
docker compose logs --no-log-prefix event-translator | grep '"Message":"Dead-lettered'   # .NET logs are JSON lines
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
| `incremental.snapshot.chunk.size=20` | an incremental snapshot reads 20 rows per chunk, so a backfill of the small demo tables spans several chunks and streaming visibly continues in between |
| `heartbeat.action.query` | updates `dbo.DebeziumHeartbeat` every 10 s, so the source always changes while the connector runs |
| `decimal.handling.mode=string` | amounts arrive as exact decimal strings |
| `tombstones.on.delete=false` | deletes are one event with the before image |

After a Connect restart, Debezium 3.6.2 fails its task when the first poll
finds no new change (`NullPointerException` in `buildEndTransactionValue`; the
code is the same in 3.7.0). `connect-watchdog` (`connect/watchdog.py`) checks
the task every 10 s and restarts it after exactly this failure, at most 3
times in 10 minutes; any other failure it leaves alone, and
`ConnectorNotRunning` fires. Its log has one line per restart:

```sh
docker compose logs connect-watchdog
# 2026-10-29T22:08:00Z restarted task 0 of hrcore-cdc after the known END-timestamp failure (1 in 600 s)
```

## Services and Scenarios

Each .NET service serves `/health/live`, `/health/ready`, and
`/system/component-identity`:

```sh
curl -s http://127.0.0.1:5101/health/ready                 # Event Translator
curl -s http://127.0.0.1:5102/health/ready                 # Pay History Projector
curl -s http://127.0.0.1:5103/health/ready                 # Pay Query API
curl -s http://127.0.0.1:5103/system/component-identity
```

The projector is ready once it has applied the read-model schema and owns
partitions; the API is ready once the schema exists.

```sh
bash scripts/scenarios/01-initial-snapshot.sh      # every seeded employee as EmployeeHired; read model matches HrCore
bash scripts/scenarios/02-raise-next-month.sh      # SalaryChanged, not retroactive
bash scripts/scenarios/03-retroactive-raise.sh     # retroactive SalaryChanged; validAt and knownAt answers
bash scripts/scenarios/04-correction.sh            # SalaryCorrected; the earlier version stays in the history
bash scripts/scenarios/05-hire-one-transaction.sh  # one EmployeeHired from three tables
bash scripts/scenarios/06-idempotency.sh           # business and raw replay; read model unchanged
bash scripts/scenarios/07-dead-letter.sh           # bad row and poison messages in hr.dlq; translator and projector go on
```

Each scenario runs its `.sql` file (safe to rerun) and checks the events and,
from scenario 3 on, the Query API. Queries are described in
[read-model.md](read-model.md).
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

## Read Model

```sh
docker compose exec postgres psql -U payhistory -d payhistory   -c "SELECT employee_id, pay_component, monthly_amount, valid_from, valid_to, recorded_from, recorded_to
      FROM pay_history WHERE tenant_id = 1 AND employee_id = 1003 ORDER BY valid_from, recorded_from;"
docker compose logs pay-history-projector | grep -E 'Applied|NoChange|Duplicate' | tail
```

The projector logs every event as `Applied`, `NoChange`, or `Duplicate`, with
its type, id, and Kafka position; never an amount.

## Replay

Rebuild the read model from the business topic:

```sh
scripts/ops/replay-projector.sh
```

The script waits until the projector has caught up and takes the read-model
checksum (`scripts/checks/readmodel-checksum.sql`: event and row counts and
an MD5 over every `pay_history` row without its surrogate key). It then
stops the projector, empties `pay_history` and `processed_event`, resets the
group to the earliest offsets, and starts the projector again. When the
projector has caught up, it compares the checksum. The same steps by hand:

```sh
docker compose stop pay-history-projector
docker compose exec postgres psql -U payhistory -d payhistory -c 'TRUNCATE pay_history, processed_event RESTART IDENTITY;'
docker compose exec kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 \
  --group cdcpay-pay-history-projector --topic hr.employee-pay.v1 --reset-offsets --to-earliest --execute
docker compose start pay-history-projector
docker compose exec kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 \
  --describe --group cdcpay-pay-history-projector   # LAG 0 on every partition: done
```

Run it while the source is quiet: a change committed during the replay
changes the checksum. The Query API answers from a partial read model until
the projector has caught up. A reset needs the group to be inactive, so the
projector is stopped first.

## Backfill

Re-read one source table without stopping streaming:

```sh
scripts/ops/backfill.sh Salary       # Tenant, Employee, Position, or Salary
```

The script inserts an `execute-snapshot` signal into `dbo.DebeziumSignal` and
waits until Connect logs that the incremental snapshot has finished. The rows
arrive with `"snapshot":"incremental"` and become snapshot events. Their ids
come from the row content (see [event-design.md](event-design.md)), so
unchanged rows are duplicates. A row that changed since the first snapshot
gets a new id, but the read model already states it, so the merge is a
no-op. A backfill adds no `pay_history` rows.
`scripts/checks/backfill-while-streaming.sh` proves both points. It commits a
raise while the snapshot runs and checks that the raise is published before
the snapshot finishes. It then checks that every row recorded since the
start belongs to the raise.

## Failure Drills

Each drill script sets up a failure, observes it, and checks the outcome;
results are in [journal.md](journal.md). Run them on a caught-up stack while
the source is otherwise quiet.

```sh
bash scripts/drills/01-connect-restart.sh   # Connect stopped mid-transaction, changes made, restarted
bash scripts/drills/02-agent-stopped.sh     # SQL Server Agent stopped, changes made, started
bash scripts/drills/03-retention-exceeded.sh  # Connect down past the CDC retention, re-snapshot
bash scripts/drills/04-projector-killed.sh  # projector killed (SIGKILL) while it works through a backlog
bash scripts/drills/05-column-without-capture-instance.sh  # column added without a new capture instance
```

Drill 1 commits a raise and stops Connect as soon as Debezium has sent its
rows, makes four more raises while Connect is down (120 s, `DOWN_SECONDS`),
and starts it again. It passes when Debezium resumes from the offset stored at
the stop, the five events are applied once, nothing is dead-lettered, and the
read model matches the source as before. It compares the source with the read
model row by row (`readmodel_divergence` in `scripts/scenarios/lib.sh`):
every `Salary` row needs exactly one current `pay_history` row. Scenario 7's
rejected row is the expected difference.

Drill 2 stops SQL Server Agent (`EXEC master.dbo.xp_servicecontrol N'STOP',
N'SQLServerAGENT'`), commits two raises, and waits for `CdcEventsStalled`.
It passes when nothing is captured or sent while Agent is down, the alert
fires, and after `xp_servicecontrol N'START'` the capture job (it starts with
Agent) catches up: both events applied once, recorded at their source commit
time, nothing dead-lettered, and the alert resolved. Drill helpers are in
`scripts/drills/lib.sh`.

Drill 3 stops Connect, commits raises, lowers the CDC cleanup retention to 1
minute, and runs the cleanup job, so it removes a change Debezium has not
read. It passes when the gap check reports the gap, the restarted task fails
and stays failed, nothing is applied, and the re-snapshot restores the read
model without dead-lettering a transaction. The retention is set back to
4320 minutes at the end, also when the drill fails.

Drill 4 stops the projector, builds a backlog of raise events (15 rounds for
3001..3012, `ROUNDS`), starts the projector, and kills it with `SIGKILL` as
soon as it has applied some of them. It passes when, after the restart, every
event is applied once, the read model matches the source, nothing is
dead-lettered, and `scripts/ops/replay-projector.sh` rebuilds an identical
history. `docker compose kill` does not trigger the restart policy, so the
drill starts the projector itself.

Drill 5 adds a column `ApprovedBy` to `dbo.Salary` without a new capture
instance, updates only that column for one employee, commits a raise, and
runs a backfill, first as is and then with the column excluded in the
connector. It drops the column and restores the connector configuration at
the end, also when it fails. See the next section for what it shows.

### Connector Down Longer Than the CDC Retention

The cleanup job deletes changes older than the retention (default 4320
minutes, `EXEC sys.sp_cdc_help_jobs`). Retention counts back from the newest
change in the database, not from the current time, and the newest transaction
before the boundary is kept. If Connect was down longer, check before starting
it:

```sh
bash scripts/checks/cdc-retention-gap.sh    # exit 1: cleanup passed the stored offset
```

Debezium checks the same at task start: the task fails with `... but this is
no longer available on the server`, the watchdog leaves it failed, and
`ConnectorNotRunning` fires. Repair with a re-snapshot:

```sh
scripts/ops/resnapshot.sh
```

It stops the connector, deletes its offsets (`DELETE
/connectors/hrcore-cdc/offsets`), and resumes it; with no offsets,
`snapshot.mode=initial` takes a new snapshot, then streams. Snapshot events
restate every row: the read model matches the source again, but missed
changes are recorded at the snapshot time, not their commit time, and a
change overwritten during the gap is gone. The heartbeat transaction that was
open at the stop never gets its END; the translator gives it up after 60 s
and ignores it. Scenario 7's rejected row is dead-lettered again by every
snapshot. `snapshot.mode=when_needed` would re-snapshot on its own; it is not
used, so a loss is never repaired silently.

### Repairing a Read Model That Misses a Transaction

A transaction that was dead-lettered as `IncompleteTransaction` is missing
from the read model, and so is every later change it would have preceded.
Repair it with a backfill of the table (`scripts/ops/backfill.sh Salary`):
the snapshot events state every row as it is now, and the merge corrects the
history. Replaying the raw topics is not a repair: it publishes the missing
transaction after the later changes of the same employee, and the merge then
replaces their open period.

## Contracts

`schemas-init` runs `contracts/scripts/register-schema.sh`: it sets
`FULL_TRANSITIVE` on `hr.employee-pay.v1-value` and registers
`contracts/avro/v1/PayEvent.avsc`. After changing the schema, regenerate the C#
classes with `bash contracts/scripts/generate-csharp.sh`.

## Contract Compatibility Gate

```sh
sh ci/schema-compat.sh http://127.0.0.1:8081   # any registry; uses a throwaway subject
```

Registers every contract version in order under `FULL_TRANSITIVE`, then requires
`contracts/examples/breaking/PayEvent.avsc` to be rejected. CI runs it against
Kafka and Schema Registry started from `docker-compose.yml` (GitHub Actions) or
as pipeline services (Woodpecker). To check one schema against the live subject:

```sh
sh contracts/scripts/check-compatibility.sh http://127.0.0.1:8081 hr.employee-pay.v1-value contracts/avro/v2/PayEvent.avsc
```

## Source Schema Change: Salary.PayComponent

Online, without stopping CDC:

```sh
bash scripts/ops/migrate-salary-paycomponent.sh
```

| Step | What happens |
|---|---|
| 1 | checks that the latest registered contract has `payComponent` (contract v2 must be live before the source changes) |
| 2 | `db/migrations/001-salary-paycomponent.sql`: adds the column (default `'Base'`) and capture instance `dbo_Salary_v2` |
| 3 | waits for the Connect log line `Migrating schema to Capture instance "dbo_Salary_v2"` (seconds) |
| 4 | `db/migrations/001-salary-paycomponent-cleanup.sql`: drops the old capture instance `dbo_Salary` |
| 5 | waits until the connector runs again: Debezium still lists the dropped change table, logs a burst of `Producer failure ... Invalid object name 'HrCore.cdc.dbo_Salary_CT'`, and restarts its task after 10 s |

After the migration, `scripts/checks/paycomponent-raise.sql` and
`scripts/checks/paycomponent-correction.sql` make changes that use the column.
The order matters: register the contract first, deploy a translator that
reads both row shapes second, change the source third, drop the old capture
instance last.

### A Column Added Without a New Capture Instance

A capture instance keeps the columns it was created with. A column added to
the table later:

- never reaches Kafka: change rows and Debezium's messages have the old
  columns, and consumers see no difference;
- is not captured even when it is the only column an update changes: SQL
  Server writes no change row, so such an update is invisible downstream;
- breaks the backfill of the table: Debezium reads the live table, finds a
  column its schema does not have (`Column 'ApprovedBy' not found in result
  set ...`), logs a warning, skips the table, and goes on streaming.
  `scripts/ops/backfill.sh` stops with that line instead of waiting. A full
  re-snapshot (`scripts/ops/resnapshot.sh`) still works and leaves the column
  out.

To carry the column into events, add a capture instance with it (as in the
`Salary.PayComponent` migration above). To keep it out but restore the
backfill, exclude it in the connector: `"column.exclude.list":
"HrCore.dbo.Salary.ApprovedBy"`.

## Topics

`kafka-init` creates `hr.employee-pay.v1` (3 partitions) and `hr.dlq`
(1 partition), both with unlimited retention so the read model can always be
rebuilt. Debezium creates the `cdc.*` topics. Broker auto-creation is off.
