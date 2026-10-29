#!/usr/bin/env bash
# Scenario 1: initial snapshot of the seeded data.
# Expected: every seeded employee appears as exactly one EmployeeHired (snapshot) event, and the
# Query API returns the salary in effect today of each seeded employee as HrCore has it.

source "$(dirname "$0")/lib.sh"

# Seeded employees are numbered 1..20 within their tenant (EmployeeId = tenant * 1000 + n);
# later hires (scenario 5: 1101) are not part of the snapshot.
expected=$(run_sql <(printf 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.Employee WHERE EmployeeId %% 1000 <= 20;\n') | tr -d '[:space:]-')

count_hired() {
  hired=$(events | grep '"eventType":"EmployeeHired"' | grep '"snapshot":true' | unique_ids)
  [ "$hired" = "$expected" ]
}

wait_for 120 "$expected snapshot EmployeeHired events" count_hired
pass "$hired distinct EmployeeHired snapshot events for $expected seeded employees"

# Every salary row of a seeded employee in effect today, as "tenant employee amount".
today=$(date -u +%Y-%m-%d)
in_effect=$(sql_rows "SELECT CONCAT(TenantId, ' ', EmployeeId, ' ', MonthlyAmount) FROM dbo.Salary
  WHERE EmployeeId % 1000 <= 20 AND EffectiveFrom <= '$today' AND (EffectiveTo IS NULL OR EffectiveTo > '$today');")

read_model_matches() {
  local tenant employee amount
  while read -r tenant employee amount; do
    api "/tenants/$tenant/employees/$employee/salary?validAt=$today" | grep -q "\"monthlyAmount\":$amount," || return 1
  done <<< "$in_effect"
}

wait_for 120 "the Query API to return today's salary of every seeded employee" read_model_matches
pass "the read model returns today's salary for all $(echo "$in_effect" | wc -l | tr -d ' ') salary rows in effect"
