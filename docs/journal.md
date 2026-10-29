# Journal: What Broke and How It Was Fixed

Document id: `journal`
Status: active
Last updated: 2026-10-29

## Purpose

An honest log of every failure met while building this PoC: symptom, cause,
and fix, newest first. Failure drills add their results here as well.

## Entries

### 2026-10-29: The Debezium task fails on every Connect restart

- **Symptom:** after `docker compose stop connect` and `start` (and after the
  Connect image was rebuilt), the connector is `RUNNING` but its task
  `FAILED` within seconds: `NullPointerException: Cannot invoke
  "java.time.Instant.toEpochMilli()" because "timestamp" is null` in
  `AbstractTransactionStructMaker.buildEndTransactionValue`. Found by the new
  `kafka_connect_task_status` metric; `ConnectorNotRunning` fired for it.
- **Cause:** with `provide.transaction.metadata`, the stored offset holds the
  transaction that was open when Connect stopped (`transaction_id` of the last
  heartbeat). On restart, the first new transaction makes Debezium 3.6.2 emit
  the END of the restored one, whose start time was not restored.
- **Workaround:** restart the task (`curl -X POST
  localhost:8083/connectors/hrcore-cdc/tasks/0/restart`); the second start
  streams normally. The END of the interrupted transaction is never emitted,
  so the translator abandons it after 60 s (`TranslatorTransactionsStuck`).
  No fix found in the 3.6 release notes. Drill 1 (stop Connect, change data,
  restart) has to decide on an upgrade or an automatic task restart.

### 2026-10-29: Scenario 7 read only part of the business topic

- **Symptom:** scenario 7 timed out waiting for the promotion of 2:2004 that
  the translator had published.
- **Cause:** `events` (Avro console consumer) stops at the first message it
  cannot decode, and scenario 7 itself puts a non-Avro message on
  `hr.employee-pay.v1`. Which events were printed before it depended on the
  partition order; it had passed by chance (332 of 455 events were read).
- **Fix:** `--skip-message-on-error` in `events`.

### 2026-10-29: The open-transaction gauge is never 0

- **Symptom:** a first `TranslatorTransactionsStuck` rule (open transactions
  above 0 for 3 minutes) fired on an idle stack.
- **Cause:** Debezium emits a transaction's END marker only when the next
  transaction begins; the heartbeat starts one every 10 s, so the translator
  always holds one open transaction.
- **Fix:** the alert counts abandoned transactions
  (`cdcpay.translator.transactions.abandoned`) instead.

### 2026-10-29: A registry 404 would have stalled the projector forever

- **Symptom:** none at run time; the compiler reported an unreachable
  pattern while the projector's failure rules were rewritten.
- **Cause:** `SchemaRegistryException` derives from `HttpRequestException`.
  The projector retried every `HttpRequestException` as "registry
  unreachable", so a message with an unknown schema id (registry 404) would
  have been retried on the same offset forever.
- **Fix:** the registry exception is classified first: 5xx, 408, and 429 are
  retried, any other status dead-letters the message. Test:
  `IsTransient_RetriesOnlyUnreachableOrOverloadedDependencies`.

### 2026-10-29: `docker compose logs --since` read UTC as local time

- **Symptom:** the first run of the backfill check reported the snapshot
  finished two seconds after the signal; it had matched the log line of an
  earlier snapshot.
- **Cause:** a `--since` time without a zone is taken as local time; the
  machine is not on UTC, so the window reached an hour back.
- **Fix:** every `--since` time carries `Z`.

### 2026-10-29: Woodpecker reports exit code 143 for the Kafka and registry services

- **Symptom:** after the first Woodpecker run with the integration step, the
  `kafka` and `schema-registry` services were shown with exit code 143;
  GitHub Actions was green.
- **Cause:** not a failure. The service logs show both integration tests and
  the contract gate using the services to the end; one second after the last
  request Woodpecker stopped the services with SIGTERM, and both JVMs shut
  down cleanly and exited with 128 + 15. PostgreSQL exits 0 on SIGTERM, so it
  looked fine.
- **Fix:** `failure: ignore` on the two services, as Woodpecker's services
  documentation suggests; the steps alone decide the result.
- **Update:** that did not work: the next run still marked both services
  failed with 143. Each service now runs its image's start script under
  `bash -c` that traps SIGTERM, forwards it to the JVM, waits, and exits 0.
  Checked with `docker stop`: Kafka exits 0 with the wrapper, 143 without;
  Schema Registry exits 0; both log a clean shutdown.

### 2026-10-29: The first read-model design would have mixed pay components

- **Symptom:** none at run time; found while writing the merge. The design
  merged every salary fact over all current rows of the employee.
- **Cause:** the design predates `payComponent` (contract v2). An allowance
  raise covers the same days as the base salary, so the merge would have
  closed the base rows and stated the allowance as the employee's only pay.
- **Fix:** the history is kept per pay component (`pay_history.pay_component`)
  and an exclusion constraint allows one current amount per component and
  day. Test: `Apply_AnotherPayComponent_LeavesBasePayAlone`. A termination
  ends every component.
- **Lesson:** a contract change needs a check of every consumer's model, not
  only of its deserializer.

### 2026-10-29: Redelivered transaction blocks the translator for a minute

- **Symptom:** after the Salary capture-instance switch, a raise and a
  correction reached the raw topics at once but became business events only
  about a minute later; the translator's committed offsets stood still, even
  for heartbeats, and nothing was dead-lettered.
- **Cause:** dropping the old capture instance made Debezium restart its task
  (see the next entry), and the restart redelivered two heartbeat
  transactions. For one, the translator saw row, duplicate row, `END`,
  duplicate `END`. The duplicate row was discarded, the first `END` released
  the transaction, and the duplicate `END` opened it again, waiting for a row
  that would never come. That phantom transaction sat at the head of the
  queue until the 60 s timeout, then was abandoned with no rows, so nothing
  was dead-lettered and nothing was logged. Found by comparing every `END`
  count with the rows on the raw topics.
- **Fix:** the assembler remembers recently released transactions and their
  rows; a repeated `END` or row of one is discarded as a duplicate, a row it
  never had is dead-lettered. Every abandoned transaction is logged, even
  without rows. Regression test:
  `Accept_WithDuplicateRowBeforeBothEnds_DoesNotBlockLaterTransactions`.
  Rerun on a fresh stack: both events arrive, nothing abandoned.
- **Lesson:** "at least once" applies to the CDC source too; the first design
  treated a late row as an error instead of a duplicate.

### 2026-10-29: Dropping the old capture instance makes Debezium restart

- **Symptom:** right after `sys.sp_cdc_disable_table` for `dbo_Salary`, Connect
  logged about 100 `Producer failure` errors within a second:
  `Invalid object name 'HrCore.cdc.dbo_Salary_CT'`, then `Going to restart
  connector after 10 sec. after a retriable exception`.
- **Cause:** Debezium had already switched to `dbo_Salary_v2` but still listed
  the old change table and queried it once more.
- **Fix:** none needed in Debezium; the restart is automatic and nothing is
  lost. The runbook waits for the connector to run again, and the translator
  handles the redelivery the restart causes (previous entry).

### 2026-10-29: Format gate fails where the build passes

- **Symptom:** `ci/dotnet-gates.sh` passed build and tests, then `dotnet format`
  failed with JSON002 "Probable JSON string detected" in the parser tests.
- **Cause:** JSON002 is an IDE analyzer that the build does not run but
  `dotnet format` does; the tests hold deliberately malformed JSON in plain
  strings.
- **Fix:** JSON002 off for test projects only. The lesson: run the whole gate
  script locally, not just build and test.

### 2026-10-29: Parse errors leaked message content into logs and headers

- **Symptom:** a poison message sent for the dead-letter check came back with
  `dlq.error: ... 'this is not a debezium event' is an invalid JSON literal`,
  and the same text was in the warning log.
- **Cause:** the parser wrapped framework exception messages, and
  System.Text.Json quotes the offending text. For a real row that text could
  be personal data, and both logs and headers must never contain row values.
- **Fix:** parse failures are described by position and kind only (`invalid
  JSON at line 0, byte 0`, `a field has a malformed value`); messages about
  unknown operation or status values no longer echo the value. Regression
  test: `ParseChange_WithMalformedMessage_DoesNotQuoteItsContent`.

### 2026-10-29: Kafka Connect killed for memory (exit 137)

- **Symptom:** on a fresh start `stack-up.sh` failed with
  `container cdc-pay-connect-1 exited (137)`; `docker inspect` showed
  `OOMKilled=true`.
- **Cause:** the Connect start script defaults to `-Xms256M -Xmx2G`, more than
  the container's 1.5 GB limit; under snapshot load the heap grew past it.
  Earlier runs had only been lucky. The Kafka default of a 1 GB heap in a 1 GB
  container had the same flaw.
- **Fix:** `HEAP_OPTS: -Xms256m -Xmx1g` for Connect and
  `KAFKA_HEAP_OPTS: -Xms512m -Xmx512m` for Kafka. Measured after the
  scenarios: Connect 1.08 of 1.5 GB, Kafka 436 MB of 1 GB.

### 2026-10-29: Translator crash loop before the raw topics exist

- **Symptom:** on a fresh stack the translator logged `BackgroundService failed`
  with `Subscribed topic not available: ^cdc\.HrCore\.dbo\..*`, the host
  stopped, and Docker restarted it until Debezium had created the topics.
- **Cause:** librdkafka reports a missing subscribed topic as a non-fatal
  error, which `Consume` throws as `ConsumeException`; the worker let it
  escape, and an escaping exception stops the host.
- **Fix:** non-fatal consume errors are logged (at most every 30 s per error
  code) and polling continues; fatal errors still stop the host. Verified on
  a fresh stack: one warning, no restart, all scenarios pass.

### 2026-10-29: `localhost` resolves to IPv6 first

- **Symptom:** a translator run on the host logged `Connect to ipv6#[::1]:29092
  failed` for several seconds before it worked.
- **Cause:** `localhost` resolves to `::1` first, but the compose port binds
  only `127.0.0.1`; the broker also advertised `localhost`, so every new
  connection tried IPv6 first.
- **Fix:** the host listener advertises `127.0.0.1:29092`, and the default
  settings use `127.0.0.1`.

### 2026-10-29: Debezium snapshot blocks every update of existing rows

- **Symptom:** the first raw-CDC smoke test hung. The heartbeat topic stayed
  at one message although the connector was `RUNNING`.
- **Cause:** `sys.dm_exec_requests` showed two `UPDATE`s waiting on
  `LCK_M_X`, blocked by a Debezium JDBC session at isolation level
  REPEATABLE READ with an open transaction holding shared key locks on every
  row of every snapshotted table. The default `snapshot.isolation.mode`
  (`repeatable_read`) left the snapshot transaction open after the snapshot,
  so its locks were never released. Debezium's own heartbeat update was
  blocked the same way, so the heartbeat was silent: the very signal meant
  to detect stalled capture was itself stalled.
- **Fix:** `ALLOW_SNAPSHOT_ISOLATION ON` for `HrCore` (`db/schema.sql`) and
  `"snapshot.isolation.mode": "snapshot"` in the connector: a consistent
  snapshot from row versions without shared locks. After a reset: no key
  locks held by Debezium sessions, no blocked requests, heartbeat updated
  every 10 s, smoke test completes.
- **Lesson:** check `sys.dm_exec_requests` for blocking before blaming CDC
  latency; a running connector can still be stuck.

### 2026-10-29: `docker compose up --wait` fails on init jobs

- **Symptom:** `docker compose up -d --wait` exited 1 with
  `container cdc-pay-kafka-init-1 exited (0)` while every service was healthy.
- **Cause:** `--wait` expects every container to stay running; a one-shot
  job that completes successfully counts as a failure.
- **Fix:** `scripts/stack-up.sh` starts the stack, checks each init job's exit
  code with `docker wait`, then waits for the health checks of the
  long-running services.

### 2026-10-29: Git Bash rewrites container paths

- **Symptom:** `docker compose exec kafka /opt/kafka/bin/kafka-topics.sh ...`
  failed with `stat C:/Program Files/Git/opt/kafka/bin/kafka-topics.sh`.
- **Cause:** Git Bash (MSYS) converts arguments that look like POSIX paths to
  Windows paths before calling `docker`.
- **Fix:** `export MSYS_NO_PATHCONV=1` before such commands (documented in
  `docs/operations.md`).

### 2026-10-29: Empty test project fails the test run

- **Symptom:** `dotnet test` exited with code 8 although every test passed.
- **Cause:** Microsoft.Testing.Platform treats "zero tests ran" as a failure
  (exit code 8). The integration-test project existed but had no tests yet.
- **Fix:** removed the empty project; it is added together with its first
  test instead of as a placeholder.

### 2026-10-29: Strict analyzers versus project conventions

- **Symptom:** the first Release build failed with CA1873, ASP0027, CA1707,
  and, once code style was enforced, IDE0160, IDE0022, IDE0008, IDE0058,
  IDE0290, and IDE0005.
- **Cause:** `.editorconfig` raises every analyzer diagnostic to a warning and
  warnings are errors. Without explicit style preferences the defaults
  disagree with modern C# (block-scoped namespaces, explicit types
  everywhere). CA1707 forbids underscores, but test names follow
  `Method_State_ExpectedBehavior`. ASP0027: .NET 10 already makes `Program`
  public. CA1873: an argument was formatted before the logger knew whether
  the level was enabled. IDE0005 at build time needs `GenerateDocumentationFile`.
- **Fix:** explicit style preferences in `.editorconfig`; IDE0022 and IDE0058
  off; CA1707 off for test projects only; `public partial class Program`
  removed; the identity object passed to the log method instead of its
  string; `GenerateDocumentationFile` on with CS1591 off (no public packages).
  Code style is now enforced by the build, so build and `dotnet format`
  agree.

### 2026-10-29: `dotnet test` refuses the VSTest runner

- **Symptom:** `Testing with VSTest target is no longer supported by
  Microsoft.Testing.Platform on .NET 10 SDK and later.`
- **Cause:** xunit.v3 4.x runs on Microsoft.Testing.Platform; the .NET 10 SDK
  needs an explicit opt-in to run it through `dotnet test`.
- **Fix:** `"test": { "runner": "Microsoft.Testing.Platform" }` in
  `src/global.json`; removed the VSTest packages (`Microsoft.NET.Test.Sdk`,
  `xunit.runner.visualstudio`). Related: xunit.v3 4.x does not add
  `using Xunit;` implicitly, so the test projects declare it.

### 2026-10-29: Topic init job exits with code 1

- **Symptom:** `kafka-init` created both topics, then failed.
- **Cause:** `kafka-topics.sh --describe` accepts one `--topic`; the job
  passed two.
- **Fix:** describe with the pattern `'hr\..*'`. The job is idempotent
  (`--if-not-exists`), so rerunning it is safe.

### 2026-10-29: Schema Registry never becomes healthy

- **Symptom:** `docker compose up --wait` failed with "container
  schema-registry is unhealthy" although its log said "Server started,
  listening for requests".
- **Cause:** the health check used `curl`; the `cp-schema-registry` 8.x image
  contains neither `curl` nor `wget` (`curl: command not found`, exit 127).
- **Fix:** an HTTP request through bash `/dev/tcp`, checking for status 200 on
  `/subjects`.

### 2026-10-29: KRaft cluster id

- **Symptom:** caught in review before the first start: the compose file had
  a readable `CLUSTER_ID`.
- **Cause:** KRaft requires a base64-encoded 16-byte UUID (22 characters).
- **Fix:** a fixed id derived from a UUIDv5, so the stack starts the same way
  every time.
