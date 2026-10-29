-- Scenario 4: correction of a wrong salary entry. Employee 1004's open salary was entered
-- 250 too low; the row is updated in place with reason Correction.
-- Safe to rerun: does nothing once the correction exists.

SET NOCOUNT ON;

UPDATE dbo.Salary SET MonthlyAmount = MonthlyAmount + 250, Reason = 'Correction'
WHERE TenantId = 1 AND EmployeeId = 1004 AND EffectiveTo IS NULL AND Reason <> 'Correction';

PRINT CASE @@ROWCOUNT WHEN 1 THEN 'Salary of 1004 corrected.' ELSE 'Salary of 1004 already corrected; nothing to do.' END;
