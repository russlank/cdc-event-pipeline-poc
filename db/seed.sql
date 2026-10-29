-- Synthetic seed data for HrCore: 3 tenants, 50 employees, salary and position history.
-- Deterministic values; dates relative to the first day of the current UTC month, so
-- "effective next month" and "three months ago" in the scenarios always hold.
-- Runs once: skipped when tenants exist.

USE HrCore;
GO

SET NOCOUNT ON;

IF EXISTS (SELECT 1 FROM dbo.Tenant)
BEGIN
    PRINT 'HrCore is already seeded; skipping.';
    RETURN;
END;

DECLARE @month0 date = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1);

BEGIN TRANSACTION;

INSERT dbo.Tenant (TenantId, Name)
VALUES (1, N'Aurora Freight'), (2, N'Birch Clinics'), (3, N'Cobalt Studios');

DECLARE @tenant int = 1, @n int, @count int, @employeeId int;
DECLARE @gender char(1), @level tinyint, @monthsAgo int, @hire date, @end date;
DECLARE @raise1 date, @raise2 date, @promoted bit, @terminated bit;
DECLARE @amount1 decimal(12, 2), @amount2 decimal(12, 2), @amount3 decimal(12, 2);

WHILE @tenant <= 3
BEGIN
    SET @count = CHOOSE(@tenant, 20, 18, 12);
    SET @n = 1;

    WHILE @n <= @count
    BEGIN
        SET @employeeId = @tenant * 1000 + @n;
        SET @gender = CASE WHEN @n % 7 = 0 THEN 'X' WHEN @n % 2 = 0 THEN 'F' ELSE 'M' END;
        SET @level = 1 + @n % 4;
        SET @monthsAgo = 4 + (@n * 7 + @tenant * 3) % 40;
        SET @hire = DATEADD(day, @n % 27, DATEADD(month, -@monthsAgo, @month0));
        SET @promoted = CASE WHEN @n % 5 = 0 AND @monthsAgo >= 13 THEN 1 ELSE 0 END;
        SET @terminated = CASE WHEN @n % 11 = 0 THEN 1 ELSE 0 END;
        SET @end = CASE WHEN @terminated = 1 THEN @month0 END;

        -- Pay changes take effect on the first of the month, 12 and 24 months after hire.
        SET @raise1 = CASE WHEN @monthsAgo >= 13 THEN DATEADD(month, 12, DATEFROMPARTS(YEAR(@hire), MONTH(@hire), 1)) END;
        SET @raise2 = CASE WHEN @monthsAgo >= 25 THEN DATEADD(month, 24, DATEFROMPARTS(YEAR(@hire), MONTH(@hire), 1)) END;

        -- A small built-in gap so the pay-gap view has something to show.
        SET @amount1 = 30000 + @level * 6000 + @tenant * 500 - CASE @gender WHEN 'F' THEN 1200 ELSE 0 END;
        SET @amount2 = ROUND(@amount1 * CASE @promoted WHEN 1 THEN 1.08 ELSE 1.03 END, 0);
        SET @amount3 = ROUND(@amount2 * 1.035, 0);

        INSERT dbo.Employee (TenantId, EmployeeId, Name, Gender, HireDate, TerminationDate)
        VALUES (@tenant, @employeeId, CONCAT(N'Employee ', @employeeId), @gender, @hire,
                CASE WHEN @terminated = 1 THEN DATEADD(day, -1, @month0) END);

        IF @promoted = 1
        BEGIN
            INSERT dbo.Position (TenantId, EmployeeId, Title, Level, EffectiveFrom, EffectiveTo)
            VALUES (@tenant, @employeeId, CHOOSE(@level, N'Associate', N'Specialist', N'Senior Specialist', N'Lead'), @level, @hire, @raise1),
                   (@tenant, @employeeId, CHOOSE(@level + 1, N'Associate', N'Specialist', N'Senior Specialist', N'Lead', N'Principal'), @level + 1, @raise1, @end);
        END
        ELSE
            INSERT dbo.Position (TenantId, EmployeeId, Title, Level, EffectiveFrom, EffectiveTo)
            VALUES (@tenant, @employeeId, CHOOSE(@level, N'Associate', N'Specialist', N'Senior Specialist', N'Lead'), @level, @hire, @end);

        INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
        VALUES (@tenant, @employeeId, @amount1, 'SEK', @hire, COALESCE(@raise1, @end), 'Hire');

        IF @raise1 IS NOT NULL
            INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
            VALUES (@tenant, @employeeId, @amount2, 'SEK', @raise1, COALESCE(@raise2, @end),
                    CASE @promoted WHEN 1 THEN 'Promotion' ELSE 'Raise' END);

        IF @raise2 IS NOT NULL
            INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
            VALUES (@tenant, @employeeId, @amount3, 'SEK', @raise2, @end, 'Raise');

        SET @n += 1;
    END;

    SET @tenant += 1;
END;

COMMIT TRANSACTION;

SELECT
    (SELECT COUNT(*) FROM dbo.Tenant)   AS Tenants,
    (SELECT COUNT(*) FROM dbo.Employee) AS Employees,
    (SELECT COUNT(*) FROM dbo.Position) AS Positions,
    (SELECT COUNT(*) FROM dbo.Salary)   AS Salaries;
GO
