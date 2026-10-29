-- After migration 001: an allowance raise for employee 1002, effective next month, so the
-- new Salary.PayComponent column has to travel through the new capture instance.
-- Safe to rerun: does nothing once the allowance row exists.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @nextMonth date = DATEADD(month, 1, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

IF NOT EXISTS (SELECT 1 FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1002 AND PayComponent = 'Allowance')
BEGIN
    BEGIN TRANSACTION;
    INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason, PayComponent)
    SELECT TenantId, EmployeeId, ROUND(MonthlyAmount * 1.02, 0), Currency, @nextMonth, NULL, 'Raise', 'Allowance'
    FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1002 AND EffectiveTo IS NULL;
    UPDATE dbo.Salary SET EffectiveTo = @nextMonth
    WHERE TenantId = 1 AND EmployeeId = 1002 AND EffectiveTo IS NULL AND EffectiveFrom < @nextMonth;
    COMMIT TRANSACTION;
    PRINT 'Allowance raise for 1002 recorded.';
END
ELSE
    PRINT 'Allowance raise for 1002 already recorded; nothing to do.';
