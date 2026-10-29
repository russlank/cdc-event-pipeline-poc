-- A raise for employee 2005 made while a backfill runs: one transaction that closes the open
-- salary period and inserts the raise. Every run adds a new raise, one month after the last,
-- and never earlier than next month.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @nextMonth date = DATEADD(month, 1, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));
DECLARE @effective date = DATEADD(month, 1,
    (SELECT EffectiveFrom FROM dbo.Salary WHERE TenantId = 2 AND EmployeeId = 2005 AND EffectiveTo IS NULL));
IF @effective < @nextMonth SET @effective = @nextMonth;

BEGIN TRANSACTION;

INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
SELECT TenantId, EmployeeId, ROUND(MonthlyAmount * 1.01, 0), Currency, @effective, NULL, 'Raise'
FROM dbo.Salary
WHERE TenantId = 2 AND EmployeeId = 2005 AND EffectiveTo IS NULL;

UPDATE dbo.Salary SET EffectiveTo = @effective
WHERE TenantId = 2 AND EmployeeId = 2005 AND EffectiveTo IS NULL AND EffectiveFrom < @effective;

COMMIT TRANSACTION;
PRINT 'Raise for 2005 effective ' + CONVERT(char(10), @effective, 23) + ' recorded.';
