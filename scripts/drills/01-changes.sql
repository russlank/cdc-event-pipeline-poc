-- Drill 1: raises for employees $(First)..$(Last) of tenant 3, one transaction each: close the open
-- salary period, insert the raise one month after its start, never earlier than next month.
-- Every run adds new raises. Set First and Last with :setvar before this script.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @nextMonth date = DATEADD(month, 1, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));
DECLARE @employee int = $(First);
DECLARE @effective date;

WHILE @employee <= $(Last)
BEGIN
    SET @effective = DATEADD(month, 1,
        (SELECT EffectiveFrom FROM dbo.Salary WHERE TenantId = 3 AND EmployeeId = @employee AND EffectiveTo IS NULL));
    IF @effective < @nextMonth SET @effective = @nextMonth;

    BEGIN TRANSACTION;

    INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
    SELECT TenantId, EmployeeId, ROUND(MonthlyAmount * 1.02, 0), Currency, @effective, NULL, 'Raise'
    FROM dbo.Salary
    WHERE TenantId = 3 AND EmployeeId = @employee AND EffectiveTo IS NULL;

    UPDATE dbo.Salary SET EffectiveTo = @effective
    WHERE TenantId = 3 AND EmployeeId = @employee AND EffectiveTo IS NULL AND EffectiveFrom < @effective;

    COMMIT TRANSACTION;
    PRINT 'Raise for ' + CONVERT(varchar(10), @employee) + ' effective ' + CONVERT(char(10), @effective, 23) + ' recorded.';

    SET @employee += 1;
END
