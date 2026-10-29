-- Every Salary row as "tenant|employee|component|salary id|amount|currency|from|to", the shape of
-- scripts/checks/readmodel-current-rows.sql. Before the pay-component migration every row is Base.

SET NOCOUNT ON;

DECLARE @component nvarchar(20) =
    CASE WHEN COL_LENGTH(N'dbo.Salary', N'PayComponent') IS NULL THEN N'''Base''' ELSE N'PayComponent' END;

EXEC (N'SELECT CONCAT_WS(''|'', TenantId, EmployeeId, ' + @component + N', SalaryId, MonthlyAmount, Currency,
    CONVERT(char(10), EffectiveFrom, 23), COALESCE(CONVERT(char(10), EffectiveTo, 23), ''-''))
FROM dbo.Salary;');
