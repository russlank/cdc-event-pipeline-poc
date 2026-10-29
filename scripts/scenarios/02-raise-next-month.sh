#!/usr/bin/env bash
# Scenario 2: normal raise effective next month.
# Expected: SalaryChanged (reason Raise) with isRetroactive = false and the previous amount.

source "$(dirname "$0")/lib.sh"

run_sql scripts/scenarios/02-raise-next-month.sql

latest_raise() {
  raise=$(events | grep -E $'^1:1001\t' | grep '"eventType":"SalaryChanged"' | grep '"snapshot":false' | tail -n 1)
  [ -n "$raise" ]
}

wait_for 60 "a streamed SalaryChanged for 1:1001" latest_raise
echo "$raise" | grep -q '"isRetroactive":false' || fail "raise is marked retroactive: $raise"
echo "$raise" | grep -q '"reason":"Raise"' || fail "raise reason is not Raise: $raise"
echo "$raise" | grep -q '"previousMonthlyAmount":{"bytes"' || fail "raise has no previous amount: $raise"
pass "SalaryChanged for 1:1001: reason Raise, isRetroactive false, previous amount present"
