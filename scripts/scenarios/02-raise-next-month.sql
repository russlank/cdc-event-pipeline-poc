-- Scenario 2: a normal raise for employee 1001, effective the first of next month.
-- One transaction: close the open salary period, insert the raise.
-- Safe to rerun: does nothing once the raise exists.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @nextMonth date = DATEADD(month, 1, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

IF EXISTS (SELECT 1 FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1001 AND EffectiveTo IS NULL AND EffectiveFrom < @nextMonth)
BEGIN
    BEGIN TRANSACTION;

    INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
    SELECT TenantId, EmployeeId, ROUND(MonthlyAmount * 1.03, 0), Currency, @nextMonth, NULL, 'Raise'
    FROM dbo.Salary
    WHERE TenantId = 1 AND EmployeeId = 1001 AND EffectiveTo IS NULL;

    UPDATE dbo.Salary SET EffectiveTo = @nextMonth
    WHERE TenantId = 1 AND EmployeeId = 1001 AND EffectiveTo IS NULL AND EffectiveFrom < @nextMonth;

    COMMIT TRANSACTION;
    PRINT 'Raise for 1001 effective ' + CONVERT(char(10), @nextMonth, 23) + ' recorded.';
END
ELSE
    PRINT 'Raise for 1001 already recorded; nothing to do.';
