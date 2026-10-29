#!/usr/bin/env bash
# Scenario 5: hire in one transaction (Employee + Salary + Position).
# Expected: exactly one event for 1:1101, an EmployeeHired with initial salary and position.

source "$(dirname "$0")/lib.sh"

run_sql scripts/scenarios/05-hire-one-transaction.sql

hire_events() {
  hire=$(events | grep -E $'^1:1101\t')
  [ -n "$hire" ]
}

wait_for 60 "events for 1:1101" hire_events
sleep 5
hire=$(events | grep -E $'^1:1101\t')
count=$(echo "$hire" | unique_ids)
[ "$count" = "1" ] || fail "expected exactly 1 event for 1:1101, got $count"
echo "$hire" | grep -q '"eventType":"EmployeeHired"' || fail "the event is not EmployeeHired: $hire"
echo "$hire" | grep -q '"initialSalary":{"CdcPay.Contracts.Events.InitialSalary"' || fail "no initial salary: $hire"
echo "$hire" | grep -q '"initialPosition":{"CdcPay.Contracts.Events.InitialPosition"' || fail "no initial position: $hire"
echo "$hire" | grep -q '"tables":\["dbo.Employee","dbo.Position","dbo.Salary"\]' || fail "source tables incomplete: $hire"
pass "exactly one EmployeeHired for 1:1101 with initial salary and position from three tables"
