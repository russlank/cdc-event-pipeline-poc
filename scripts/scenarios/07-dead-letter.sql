-- Scenario 7, part 1: a row that the source accepts but the translator rejects. Employee 2002
-- gets a raise in GBP, which is not a supported currency. One transaction, like a normal raise.
-- Safe to rerun: does nothing once the GBP raise exists.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @effective date = DATEADD(month, 2, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

IF NOT EXISTS (SELECT 1 FROM dbo.Salary WHERE TenantId = 2 AND EmployeeId = 2002 AND Currency = 'GBP')
BEGIN
    BEGIN TRANSACTION;

    INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
    SELECT TenantId, EmployeeId, ROUND(MonthlyAmount / 12, 0), 'GBP', @effective, NULL, 'Raise'
    FROM dbo.Salary
    WHERE TenantId = 2 AND EmployeeId = 2002 AND EffectiveTo IS NULL;

    UPDATE dbo.Salary SET EffectiveTo = @effective
    WHERE TenantId = 2 AND EmployeeId = 2002 AND EffectiveTo IS NULL AND EffectiveFrom < @effective;

    COMMIT TRANSACTION;
    PRINT 'GBP raise for 2002 recorded.';
END
ELSE
    PRINT 'GBP raise for 2002 already recorded; nothing to do.';
