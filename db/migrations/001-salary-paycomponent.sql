-- Migration 001, steps 1-2: add Salary.PayComponent and a second capture instance with it.
-- SQL Server CDC does not pick up new columns: the existing capture instance (dbo_Salary)
-- keeps capturing the old column list. A table can have at most two capture instances.
-- Prerequisites: contract v2 registered and a translator that reads both row shapes.
-- Idempotent. Step 3 (drop the old instance) is 001-salary-paycomponent-cleanup.sql, run
-- only after Debezium has switched to dbo_Salary_v2.

USE HrCore;
GO

IF COL_LENGTH(N'dbo.Salary', N'PayComponent') IS NULL
    ALTER TABLE dbo.Salary ADD PayComponent varchar(20) NOT NULL
        CONSTRAINT DF_Salary_PayComponent DEFAULT ('Base');
GO

IF NOT EXISTS (SELECT 1 FROM cdc.change_tables WHERE capture_instance = N'dbo_Salary_v2')
    EXEC sys.sp_cdc_enable_table
        @source_schema = N'dbo',
        @source_name = N'Salary',
        @role_name = NULL,
        @capture_instance = N'dbo_Salary_v2',
        @supports_net_changes = 0;
GO

SELECT capture_instance, start_lsn, create_date
FROM cdc.change_tables
WHERE source_object_id = OBJECT_ID(N'dbo.Salary')
ORDER BY create_date;
GO
