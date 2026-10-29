-- Fingerprint of the read model: the number of processed events and of pay_history rows, and
-- an MD5 over every pay_history row in a fixed order, without the surrogate key. The same
-- fingerprint before and after a replay means an identical history.
SET TIME ZONE 'UTC';
SELECT (SELECT count(*) FROM processed_event) || ' events, ' || count(*) || ' rows, ' ||
       md5(coalesce(string_agg(
           concat_ws('|', tenant_id, employee_id, pay_component, salary_id, monthly_amount, currency,
                     valid_from, coalesce(valid_to::text, '-'), recorded_from, coalesce(recorded_to::text, '-'), event_id),
           E'\n' ORDER BY tenant_id, employee_id, pay_component, valid_from, recorded_from, recorded_to NULLS LAST, event_id), ''))
FROM pay_history;
