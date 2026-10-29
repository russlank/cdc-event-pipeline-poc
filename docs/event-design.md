# Event Design

Document id: `event-design`
Status: draft
Last updated: 2026-10-29

## Purpose

The topics, business events, envelope, identity rules, and compatibility
rules of the pipeline.

## Topics

| Topic | Content | Key | Partitions |
|---|---|---|---|
| `cdc.HrCore.dbo.<Table>` | Raw Debezium change events (JSON) | source primary key | 1 |
| `cdc.transaction` | Debezium transaction begin/end markers | transaction id | 1 |
| `hr.employee-pay.v1` | Business events (Avro) | `TenantId:EmployeeId` | 3 |
| `hr.dlq` | Messages that could not be processed, with error headers | original key | 1 |

## Business Events

| Event | Raised when |
|---|---|
| `EmployeeHired` | a new `Employee` row, with its initial `Salary` and `Position` in the same transaction |
| `SalaryChanged` | a new `Salary` row with reason `Raise` or `Promotion` |
| `SalaryCorrected` | a `Salary` row corrected in place (reason `Correction`) |
| `PositionChanged` | a new `Position` row |
| `EmployeeTerminated` | `TerminationDate` set |

## Envelope

To be written with the translator.

## Event Ids

To be written with the translator.

## Compatibility

To be written with the contracts.

## Dead-Letter Reasons

To be written with the translator.
