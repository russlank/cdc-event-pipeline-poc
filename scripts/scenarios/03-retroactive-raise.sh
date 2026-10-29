#!/usr/bin/env bash
# Scenario 3: raise recorded today, effective three months ago.
# Expected: SalaryChanged with isRetroactive = true; the Query API returns the new amount for a
# date in the backdated period, and the old amount when asked as known just before the raise.

source "$(dirname "$0")/lib.sh"

run_sql scripts/scenarios/03-retroactive-raise.sql

effective=$(sql_value "SELECT CONVERT(char(10), DATEADD(month, -3, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1)), 23);")
old=$(sql_value "SELECT MonthlyAmount FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1003 AND EffectiveTo = '$effective';")
new=$(sql_value "SELECT MonthlyAmount FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1003 AND EffectiveFrom = '$effective' AND Reason = 'Raise';")
[ -n "$old" ] && [ -n "$new" ] || fail "no retroactive raise for 1003 effective $effective in HrCore"

retroactive_raise() {
  raise=$(events | grep -E $'^1:1003\t' | grep '"eventType":"SalaryChanged"' | grep '"isRetroactive":true' | tail -n 1)
  [ -n "$raise" ]
}

wait_for 60 "a retroactive SalaryChanged for 1:1003" retroactive_raise
recorded_ms=$(echo "$raise" | grep -oE '"recordedAt":[0-9]+' | grep -oE '[0-9]+')
known_before=$(iso_millis $((recorded_ms - 1)))

current_is_new() {
  api "/tenants/1/employees/1003/salary?validAt=$effective" | grep -q "\"monthlyAmount\":$new,"
}

wait_for 60 "the Query API to return $new for 1:1003 on $effective" current_is_new
api "/tenants/1/employees/1003/salary?validAt=$effective&knownAt=$known_before" | grep -q "\"monthlyAmount\":$old," \
  || fail "as known at $known_before, 1:1003 on $effective should be $old"
pass "SalaryChanged for 1:1003 is retroactive; on $effective the salary is $new, and was $old as known at $known_before"
