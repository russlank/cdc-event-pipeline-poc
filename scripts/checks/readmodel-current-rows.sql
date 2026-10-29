-- Every pay_history row as currently known, in the shape of scripts/checks/source-salary-rows.sql.
-- A caught-up read model has exactly one such row per Salary row of the source.
SELECT concat_ws('|', tenant_id, employee_id, pay_component, salary_id, monthly_amount, currency,
                 valid_from, coalesce(valid_to::text, '-'))
FROM pay_history
WHERE recorded_to IS NULL;
