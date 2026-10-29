#!/usr/bin/env bash
# Scenario 4: correction of a wrong salary entry.
# Expected: SalaryCorrected; the Query API returns the corrected amount, and the salary history
# still holds the earlier version, closed in recorded time.

source "$(dirname "$0")/lib.sh"

run_sql scripts/scenarios/04-correction.sql

corrected=$(sql_value "SELECT MonthlyAmount FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1004 AND EffectiveTo IS NULL AND Reason = 'Correction';")
[ -n "$corrected" ] || fail "no corrected salary for 1004 in HrCore"
original=$(sql_value "SELECT CAST($corrected - 250 AS decimal(12, 2));")
today=$(date -u +%Y-%m-%d)

correction_event() {
  events | grep -E $'^1:1004\t' | grep -q '"eventType":"SalaryCorrected"'
}

current_is_corrected() {
  api "/tenants/1/employees/1004/salary?validAt=$today" | grep -q "\"monthlyAmount\":$corrected,"
}

wait_for 60 "a SalaryCorrected for 1:1004" correction_event
wait_for 60 "the Query API to return $corrected for 1:1004 today" current_is_corrected

# One version per line; the earlier one must still be there, with recordedTo set.
versions=$(api "/tenants/1/employees/1004/salary-history" | sed 's/},{/}\n{/g')
echo "$versions" | grep "\"monthlyAmount\":$original," | grep -q '"recordedTo":"' \
  || fail "salary history of 1:1004 has no closed version with $original"
echo "$versions" | grep "\"monthlyAmount\":$corrected," | grep -q '"recordedTo":null' \
  || fail "salary history of 1:1004 has no current version with $corrected"
pass "SalaryCorrected for 1:1004: current salary $corrected; the earlier version $original is kept in the history"
