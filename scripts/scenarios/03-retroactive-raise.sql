-- Scenario 3: a raise for employee 1003, recorded today, effective the first of the month
-- three months ago. One transaction: close the open salary period, insert the raise.
-- Safe to rerun: does nothing once the raise exists.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @effective date = DATEADD(month, -3, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

IF EXISTS (SELECT 1 FROM dbo.Salary WHERE TenantId = 1 AND EmployeeId = 1003 AND EffectiveTo IS NULL AND EffectiveFrom < @effective)
BEGIN
    BEGIN TRANSACTION;

    INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
    SELECT TenantId, EmployeeId, ROUND(MonthlyAmount * 1.04, 0), Currency, @effective, NULL, 'Raise'
    FROM dbo.Salary
    WHERE TenantId = 1 AND EmployeeId = 1003 AND EffectiveTo IS NULL;

    UPDATE dbo.Salary SET EffectiveTo = @effective
    WHERE TenantId = 1 AND EmployeeId = 1003 AND EffectiveTo IS NULL AND EffectiveFrom < @effective;

    COMMIT TRANSACTION;
    PRINT 'Retroactive raise for 1003 effective ' + CONVERT(char(10), @effective, 23) + ' recorded.';
END
ELSE
    PRINT 'Retroactive raise for 1003 already recorded; nothing to do.';
