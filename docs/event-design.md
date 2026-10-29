# Event Design

Document id: `event-design`
Status: active
Last updated: 2026-10-29

## Purpose

The topics, business events, envelope, identity rules, translation rules, and
compatibility rules of the pipeline. The schemas are
`contracts/avro/v1/PayEvent.avsc` and `contracts/avro/v2/PayEvent.avsc`; producers
write v2.

## Topics

| Topic | Content | Key | Partitions |
|---|---|---|---|
| `cdc.HrCore.dbo.<Table>` | raw Debezium change events (JSON, no schema) | source primary key | 1 |
| `cdc.transaction` | Debezium transaction `BEGIN`/`END` markers with event counts per table | transaction id | 1 |
| `hr.employee-pay.v1` | business events (Avro, Schema Registry) | `TenantId:EmployeeId` | 3 |
| `hr.dlq` | messages that could not be translated, unchanged, with `dlq.*` headers | original key | 1 |

The key `TenantId:EmployeeId` keeps all events of one employee in order on one
partition. Raw and business topics keep data forever (`retention.ms = -1`), so
the read model can always be rebuilt.

## Business Events

| Event | Raised when | Payload |
|---|---|---|
| `EmployeeHired` | an `Employee` insert, with its initial `Salary` (reason `Hire`) and `Position` in the same transaction | `hireDate`, `gender`, `initialSalary` (`salaryId`, `monthlyAmount`, `currency`, `payComponent`), `initialPosition` (`positionId`, `title`, `level`) |
| `SalaryChanged` | a new `Salary` row with reason `Raise` or `Promotion` | `salaryId`, `monthlyAmount`, `currency`, `reason`, `previousMonthlyAmount`, `payComponent` |
| `SalaryCorrected` | a `Salary` row updated in place with reason `Correction` | `salaryId`, `monthlyAmount`, `currency`, `changedFields`, `previousMonthlyAmount`, `previousEffectiveFrom`, `previousEffectiveTo`, `payComponent` |
| `PositionChanged` | a new `Position` row | `positionId`, `title`, `level`, `previousTitle`, `previousLevel` |
| `EmployeeTerminated` | `Employee.TerminationDate` set | `terminationDate` |

Events carry business meaning, not column diffs. Names never appear in events.
`gender` is recorded at hire for the pay-gap view; later gender changes are
not propagated (a known limitation of this PoC).

## Envelope

| Field | Meaning |
|---|---|
| `eventId` | deterministic UUID (see Event Ids) |
| `eventType` | one of the five event names; names the `payload` branch |
| `schemaVersion` | contract version from `contracts/avro` (`2` since `payComponent`; `1` before) |
| `tenantId`, `employeeId` | the employee the fact is about |
| `effectiveFrom`, `effectiveTo` | **valid time**: when the fact applies; `effectiveTo` is exclusive, null = open-ended |
| `recordedAt` | **transaction time**: the source commit time (UTC); for snapshot events the snapshot read time |
| `isRetroactive` | `effectiveFrom` is before the UTC date of `recordedAt`; always false for snapshot events |
| `source` | `tables`, `commitLsn`, `changeLsn`, `transactionId`, `snapshot` |
| `payload` | union of the five payload records |

Debezium does not report which SQL Server capture instance a change came
from, so the envelope has no capture-instance field.

## Event Ids

`eventId` is a name-based UUID (version 5) in a fixed project namespace:

- **Streamed changes**: from `commit_lsn | change_lsn | table | primary key` of
  the row that anchors the fact (the `Employee` row of a hire, the new
  `Salary` or `Position` row, the corrected row). The same change always gets
  the same id, so a replay or a redelivery can be deduplicated.
- **Snapshot rows**: from `snapshot | event type | table | primary key |
  business columns`. Every snapshot reads at a new LSN, so the position cannot
  identify a row; the content can. A re-snapshot of unchanged data yields the
  same ids.

## Translation Rules

The translator reads all `cdc.HrCore.dbo.*` topics and `cdc.transaction`. Rows
of one source transaction can arrive on several topics in any order; a
transaction is complete when its `END` marker has arrived and every per-table
count it announces is met. Complete transactions are translated in commit
order. Rows are then grouped per employee and each group must match one shape:

| Shape | Result |
|---|---|
| `Employee` insert (+ one `Salary` insert with reason `Hire`, + one `Position` insert) | `EmployeeHired` |
| `Salary` insert with reason `Raise` or `Promotion` (+ updates that only close earlier periods) | `SalaryChanged` |
| the same plus a `Position` insert (a promotion) | `SalaryChanged` and `PositionChanged`, same `transactionId` |
| `Position` insert (+ closing updates) | `PositionChanged` |
| `Salary` update with reason `Correction` changing `MonthlyAmount`, `EffectiveFrom`, and/or `EffectiveTo`, and nothing else, alone in its transaction | `SalaryCorrected` |
| `Employee` update setting `TerminationDate` (+ closing updates) | `EmployeeTerminated` |
| `Employee` update of `Name` or `Gender` only | ignored |
| `Tenant` rows, Debezium heartbeat and signal rows | ignored |
| snapshot rows | one event per row: `EmployeeHired` (+ `EmployeeTerminated` if terminated), `SalaryChanged`, `PositionChanged`; `source.snapshot = true` |

A "closing update" changes only `EffectiveTo`. Everything else goes to the
dead-letter topic; nothing is dropped silently. Ignored rows are counted with
a reason.

Debezium and Kafka deliver at least once: after a Debezium task restart the
same rows and `END` markers can arrive again. A row repeated within a
transaction, and any row or `END` of a recently released transaction (it
remembers the last 10 000), is discarded as a duplicate. A row that the
released transaction never had is dead-lettered.

## Dead-Letter Reasons

Each dead-lettered message keeps its original key and value. Headers
(`CdcPay.Contracts.DeadLetterHeaders`): `dlq.reason`, `dlq.error`,
`dlq.component` (`event-translator` or `pay-history-projector`),
`dlq.source.topic`, `dlq.source.partition`, `dlq.source.offset`,
`dlq.timestamp`.

`dlq.error` and every log message may name values from closed code domains:
currency, reason, operation, table, event type. They never contain row
content: amounts, dates, names, titles, or free text. A rejected code is
outside its domain and could be anything, so it is named only when it has the
shape of a code (a letter, then at most 31 letters, digits, or underscores;
`CdcPay.Contracts.DiagnosticValues.Code`). Otherwise the text says
`(not a code)`: `Salary.Currency 'GBP' is not supported`, but
`Salary.Reason (not a code) is not one of Hire, Raise, Promotion, Correction`.
`DiagnosticSafetyTests` enforces the rule: every dead-letter error is built
from rows full of recognisable content, and log messages may take only
bounded parameters (counts, ids, positions, codes, types).

From the translator (raw CDC topics):

| `dlq.reason` | When |
|---|---|
| `Unparseable` | not a Debezium change or transaction event of the expected shape |
| `InvalidRow` | a value the business rules reject: unknown `Reason`, unsupported `Currency`, amount not positive, a period that ends before it starts |
| `InvalidCorrection` | a correction that changes another column, changes nothing, or shares its transaction with other changes |
| `UnsupportedChange` | a delete, a row moved to another employee, or an untranslated table (legitimate deletes such as erasure requests would need a dedicated event) |
| `UnclassifiedTransaction` | rows that match none of the shapes above |
| `IncompleteTransaction` | a transaction still incomplete after the timeout (default 60 s), or a row that its already released transaction did not have |

From the projector (`hr.employee-pay.v1`; the message's own headers are kept as well):

| `dlq.reason` | When |
|---|---|
| `Undeserializable` | not Avro of a registered contract version (bad framing, unknown schema id), or an empty value |
| `InvalidEvent` | valid Avro, but the payload does not match `eventType` |

A replay of raw topics (scenario 6) dead-letters the same bad messages again;
`dlq.source.*` identifies repeats.

## Compatibility

- The subject `hr.employee-pay.v1-value` uses `FULL_TRANSITIVE`: every new
  version must be readable by consumers of all earlier versions and must read
  all earlier data. "Old consumers keep working" is forward compatibility,
  which `BACKWARD` alone does not guarantee.
- Safe change: add a field with a default to a payload record. Contract v2 does
  exactly that: `payComponent` (default `"Base"`) in `InitialSalary`,
  `SalaryChangedPayload`, and `SalaryCorrectedPayload`. A v1 reader ignores the
  field; a v2 reader of v1 data gets `"Base"`. Unit tests check both
  directions, and the registry accepts v2 under `FULL_TRANSITIVE`.
- Not possible under this mode: a new event type. A new union branch is
  backward but not forward compatible, because an old reader cannot resolve
  the unknown branch. A new event type therefore needs a new topic version or
  one subject per event type (`TopicRecordNameStrategy`).
- Breaking change: `contracts/examples/breaking/PayEvent.avsc` renames
  `monthlyAmount` to `amount`; its README explains why consumers would break.
  The registry rejects it with HTTP 409. It reports `MISSING_UNION_BRANCH`,
  because a union branch is matched by full record name and the changed
  record no longer resolves against the old branch.
- CI (`ci/schema-compat.sh`) registers every version in `contracts/avro` in
  order on a throwaway subject and requires the breaking example to be
  rejected; a pull request that adds an incompatible version fails.
- Producers never register schemas. `contracts/scripts/register-schema.sh`
  (the `schemas-init` job) is the only way a version enters the registry, and
  the serializer looks up the id of the exact schema it was generated from.
- The C# classes in `src/CdcPay.Contracts/Generated` are generated from the
  latest schema (`contracts/scripts/generate-csharp.sh`); a unit test fails when
  the two differ.

## Source Schema Changes

SQL Server CDC does not pick up new columns: a capture instance keeps the
column list it was created with, and a table allows at most two capture
instances. Adding `Salary.PayComponent` therefore takes a second capture
instance (`dbo_Salary_v2`) and a switch-over; the runbook is
`scripts/ops/migrate-salary-paycomponent.sh` (see `docs/operations.md`). During
the switch rows arrive with and without the column on the same topic; the
translator reads a missing column as `"Base"`, so both shapes produce valid v2
events. Snapshot event ids include `PayComponent` only when it is not
`"Base"`, so a re-snapshot after the migration keeps the ids of unchanged rows.
