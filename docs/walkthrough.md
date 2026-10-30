# Pipeline Walkthrough

Document id: `walkthrough`
Status: active
Last updated: 2026-10-30

## Purpose

A guided run through the whole pipeline: from a raise typed into SQL Server
to the business event, its schema, the as-of answer of the read model, and an
alert when something goes wrong. One script does it; this page says what each
step shows and where to look.

## Run It

```sh
cp .env.example .env              # once; set the passwords
bash scripts/walkthrough.sh --fresh          # reset the stack first (docker compose down -v)
bash scripts/walkthrough.sh --fresh --pause  # the same, waiting for Enter between steps (presenting)
bash scripts/walkthrough.sh                  # on a running stack
```

From a clean stack it takes about three minutes once the images are built
or pulled (166 s measured; the first build and image pulls take longer).
Every step ends with `PASS` or stops with `FAIL` and the reason.

## Steps

| Step | What it does | What to look at |
|---|---|---|
| 1. Start the stack | `scripts/stack-up.sh`: SQL Server with CDC, Kafka, Debezium, Schema Registry, translator, projector, PostgreSQL, Query API, monitoring | `docker compose ps`; Kafka UI at <http://127.0.0.1:8080> |
| 2. Initial snapshot | scenario 1: Debezium's snapshot of the seed data becomes one `EmployeeHired` per employee, and the read model matches `HrCore` | the business topic `hr.employee-pay.v1` in Kafka UI |
| 3. Retroactive raise | scenario 3: a raise for employee 1003, recorded today, effective the first of the month three months ago | the event `SalaryChanged` with `isRetroactive: true` |
| 4. What it produced | prints the event (amounts left out: Avro decimals print as bytes; dates and times translated), the subject's versions and compatibility in the Schema Registry, and three Query API calls: the salary on the backdated date as known now, as known just before the raise, and the full history | the two answers differ: the history keeps what was known when |
| 5. A failure | drill 7 without the wait for resolution: poison messages on the raw, transaction, and business topics go to `hr.dlq` with their reason; a raise still flows; `DlqNotEmpty` fires | `docker compose logs alert-sink`, Alertmanager at <http://127.0.0.1:9093> |
| 6. Where to look next | prints the URLs | Grafana dashboard "CDC Pay Pipeline" at <http://127.0.0.1:3000> |

## After the Walkthrough

- The alert of step 5 resolves about 5 minutes after the last dead letter.
- Other scenarios: `scripts/scenarios/` (normal raise, correction, hire in
  one transaction, replay, dead letters); see [operations.md](operations.md).
- Failure drills: `scripts/drills/` (Connect restart, SQL Server Agent
  stopped, CDC retention exceeded, projector killed, column without a capture
  instance, incompatible schema, poison messages); results in
  [journal.md](journal.md).
- Reset: `docker compose down -v` removes all data.
