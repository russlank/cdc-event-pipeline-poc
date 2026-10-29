-- Scenario 5: hire employee 1101 in tenant 1 in one transaction across three tables.
-- Safe to rerun: does nothing once the employee exists.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @start date = DATEADD(month, 1, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

IF NOT EXISTS (SELECT 1 FROM dbo.Employee WHERE TenantId = 1 AND EmployeeId = 1101)
BEGIN
    BEGIN TRANSACTION;

    INSERT dbo.Employee (TenantId, EmployeeId, Name, Gender, HireDate, TerminationDate)
    VALUES (1, 1101, N'Employee 1101', 'F', @start, NULL);

    INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
    VALUES (1, 1101, 41000.00, 'SEK', @start, NULL, 'Hire');

    INSERT dbo.Position (TenantId, EmployeeId, Title, Level, EffectiveFrom, EffectiveTo)
    VALUES (1, 1101, N'Specialist', 2, @start, NULL);

    COMMIT TRANSACTION;
    PRINT 'Employee 1101 hired from ' + CONVERT(char(10), @start, 23) + '.';
END
ELSE
    PRINT 'Employee 1101 already exists; nothing to do.';
