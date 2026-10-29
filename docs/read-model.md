# Read Model and Query API

Document id: `read-model`
Status: active
Last updated: 2026-10-29

## Purpose

How the Pay History Projector turns business events into a bitemporal pay
history in PostgreSQL, and how the Pay Query API answers as-of questions.

## Two Time Axes

Every row has two periods, both half-open `[from, to)`, with null meaning
open-ended:

| Axis | Columns | Meaning | Source |
|---|---|---|---|
| Valid time | `valid_from`, `valid_to` (dates) | when the amount applies | the event's `effectiveFrom`, `effectiveTo` |
| Recorded time | `recorded_from`, `recorded_to` (UTC instants) | when the read model learned it | the event's `recordedAt`, never earlier than the version it replaces |

A new fact never overwrites a row. It closes the rows it replaces
(`recorded_to` is set) and inserts what is now known. The projector never
writes its own clock into `pay_history`, so replaying the same events in the
same order gives the same rows.

## Tables

The schema is `db/readmodel/*.sql`. The projector applies it at startup, and
every statement is idempotent.

| Table | Content |
|---|---|
| `processed_event` | one row per applied event id, with type, employee, and `recordedAt`: the idempotency guard |
| `pay_history` | one row per version of one pay component's amount: `tenant_id`, `employee_id`, `pay_component`, `salary_id`, `monthly_amount`, `currency`, both periods, and the `event_id` that stated the amount |

The history is kept per pay component (`Base`, `Allowance`, and so on; contract
v2). An exclusion constraint guarantees that, as currently known, a component
has at most one amount per day.

## Projection

For every event, in one database transaction:

1. Insert the event id into `processed_event`. If it is already there, the
   event was seen before: roll back and skip it.
2. Map the event to a pay fact. `EmployeeHired` with an initial salary,
   `SalaryChanged`, and `SalaryCorrected` are salary facts for their valid
   period. `EmployeeTerminated` ends pay. `PositionChanged`, and a snapshot
   `EmployeeHired` (whose salary history arrives as separate `SalaryChanged`
   events), say nothing about pay. They are only recorded.
3. Lock the employee's current rows and apply the merge:
   - **Salary fact** for valid period `V` of component `C`: close the current
     rows of `C` that overlap `V`, insert their parts outside `V` again, and
     insert the fact for `V`. If those rows already state the same amount and
     currency for all of `V`, the fact is a no-op; snapshot re-reads and
     backfills are no-ops for this reason.
   - **Correction**: first retract the corrected salary row (its current
     versions are closed and not inserted again, so a shortened period leaves
     no stale value), then merge the corrected fact as above.
   - **Termination**: every component ends on the day after the termination
     date (`Employee.TerminationDate` is the last day of employment). Periods
     that start later are retracted.
4. Commit. Only then is the event's Kafka offset stored for the next commit.

Two rules keep recorded time consistent:

- The recorded instant of a change is the event's `recordedAt`, but never
  earlier than the `recorded_from` of the rows it replaces. A snapshot row
  can be read after a change that committed during the snapshot, and a
  recorded period must not run backwards.
- A row that is replaced at the same instant it was recorded was never
  visible on its own, so it is deleted instead of closed.

### What `recordedFrom` and `knownAt` Mean

`recordedFrom` is the instant the read model learned the fact, and it is
never earlier than the `recordedFrom` of the version the fact replaces. It is
still derived from the events, never from the projector's clock. Usually it
equals the source commit time. It is later when the version being replaced
was itself recorded later: a snapshot row carries the time the snapshot read
it, so a change that committed while the snapshot ran is recorded at that
read time, not at its own commit.

So `knownAt` asks what the read model knew at that instant, not when the
source database committed the change. Between a change's source commit and
the snapshot read time, `knownAt` still returns the earlier version. To ask
what the source held at a given moment, use the event's `recordedAt` on
`hr.employee-pay.v1`.

The merge is a pure function (`src/CdcPay.PayHistory/PayHistoryMerge.cs`) with
unit tests for each case. The store only runs its plan.

## Failure Handling

| Situation | Behaviour |
|---|---|
| The same event delivered again (replay, redelivery, backfill of unchanged rows) | `processed_event` conflict: skipped, logged as `Duplicate`, counted in `cdcpay.projector.events.duplicate` |
| PostgreSQL unreachable, failing over, or a serialization conflict; Schema Registry unreachable, 5xx, 408, or 429 | retried every 2 s on the same message; never dead-lettered |
| Not Avro of a registered contract version, or an empty value | to `hr.dlq` with reason `Undeserializable`; the projector moves on |
| A payload that does not match `eventType` | to `hr.dlq` with reason `InvalidEvent`; the projector moves on |
| Any other database error (a bug, a broken constraint) | logged as critical with the error type only; the host stops and restarts from the last committed offset |

A dead-lettered message's offset is committed like a projected one. The
projector's counters (meter `CdcPay.PayHistoryProjector`) are
`cdcpay.projector.events.applied` (tag `result`: `changed` or `unchanged`),
`cdcpay.projector.events.duplicate`, and `cdcpay.projector.dlq.produced`
(tag `reason`).

## Query API

The API runs on `127.0.0.1:5103` and connects with a read-only login.

| Request | Answer |
|---|---|
| `GET /tenants/{t}/employees/{e}/salary?validAt=D` | pay in effect on date `D`, as currently known |
| `GET /tenants/{t}/employees/{e}/salary?validAt=D&knownAt=K` | pay in effect on `D`, as known at instant `K` |
| `GET /tenants/{t}/employees/{e}/salary-history` | every version, ordered by valid from, recorded from, pay component |

- `validAt` is a date, `yyyy-MM-dd`.
- `knownAt` is an ISO 8601 date-time, read as UTC when it has no offset. A bare
  date means the end of that day UTC. A `+` offset must be URL-encoded as `%2B`.
- `salary` returns one entry per pay component in `components`. The list is
  empty when no pay applies that day, for example after a termination.

```sh
curl -s 'http://127.0.0.1:5103/tenants/1/employees/1003/salary?validAt=2026-07-01'
curl -s 'http://127.0.0.1:5103/tenants/1/employees/1003/salary?validAt=2026-07-01&knownAt=2026-10-29T15:00:00Z'
curl -s 'http://127.0.0.1:5103/tenants/1/employees/1003/salary-history'
```

Each version has `payComponent`, `monthlyAmount`, `currency`, `validFrom`,
`validTo`, `recordedFrom`, `recordedTo`, `salaryId`, and `eventId`.

Errors are RFC 9457 problem details (`application/problem+json`) with a
stable `code` and the request's `traceId`:

| Status | `code` | When |
|---|---|---|
| 400 | `validAt-required` | `validAt` missing |
| 400 | `validAt-invalid` | `validAt` is not `yyyy-MM-dd` |
| 400 | `knownAt-invalid` | `knownAt` is neither a date-time nor a date |
| 404 | `employee-not-found` | the projector has applied no event about the employee |

## Known Limits

- `recordedAt` of snapshot events is the time of the snapshot, so the read
  model knows nothing earlier than the first snapshot. A `knownAt` before it
  returns no pay.
- `PositionChanged` does not reach the read model; it holds pay only.
