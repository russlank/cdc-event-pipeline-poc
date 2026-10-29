-- After migration 001: correct the amount of the allowance raise of employee 1002.
-- Safe to rerun: does nothing once the correction exists.

SET NOCOUNT ON;

UPDATE dbo.Salary SET MonthlyAmount = MonthlyAmount + 100, Reason = 'Correction'
WHERE TenantId = 1 AND EmployeeId = 1002 AND PayComponent = 'Allowance' AND Reason = 'Raise';

PRINT CASE @@ROWCOUNT WHEN 1 THEN 'Allowance raise of 1002 corrected.' ELSE 'Nothing to correct.' END;
