-- Raw CDC smoke test: one multi-row transaction, an update, an insert, and a delete.
-- Run against a seeded HrCore, then inspect the cdc.* topics and cdc.transaction.
--   docker compose exec -T sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b' < scripts/checks/raw-cdc-smoke.sql
-- Changes data: rerun only on a fresh stack, or accept extra rows.

USE HrCore;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @month0 date = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1);
DECLARE @nextMonth date = DATEADD(month, 1, @month0);

-- 1. One transaction, two rows: close the open salary of employee 3012 and add a raise.
BEGIN TRANSACTION;
UPDATE dbo.Salary SET EffectiveTo = @nextMonth
WHERE TenantId = 3 AND EmployeeId = 3012 AND EffectiveTo IS NULL;
INSERT dbo.Salary (TenantId, EmployeeId, MonthlyAmount, Currency, EffectiveFrom, EffectiveTo, Reason)
SELECT TenantId, EmployeeId, ROUND(MonthlyAmount * 1.02, 0), Currency, @nextMonth, NULL, 'Raise'
FROM dbo.Salary
WHERE TenantId = 3 AND EmployeeId = 3012 AND EffectiveTo = @nextMonth;
COMMIT TRANSACTION;

-- 2. Single-row update.
UPDATE dbo.Employee SET Name = N'Employee 3012 (renamed)' WHERE TenantId = 3 AND EmployeeId = 3012;

-- 3. Insert, then delete, in two transactions.
INSERT dbo.Tenant (TenantId, Name) VALUES (99, N'Smoke Test Tenant');
DELETE dbo.Tenant WHERE TenantId = 99;

SELECT SalaryId, EmployeeId, MonthlyAmount, EffectiveFrom, EffectiveTo, Reason
FROM dbo.Salary WHERE TenantId = 3 AND EmployeeId = 3012 ORDER BY EffectiveFrom;
