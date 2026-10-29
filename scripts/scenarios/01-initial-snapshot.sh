#!/usr/bin/env bash
# Scenario 1: initial snapshot of the seeded data.
# Expected: every seeded employee appears as exactly one EmployeeHired (snapshot) event.

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
