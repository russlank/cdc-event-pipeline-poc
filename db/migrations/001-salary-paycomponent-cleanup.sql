-- Migration 001, step 3: drop the old Salary capture instance once Debezium streams from
-- dbo_Salary_v2 (its log says it switched, and new rows carry PayComponent). Idempotent.

USE HrCore;
GO

IF EXISTS (SELECT 1 FROM cdc.change_tables WHERE capture_instance = N'dbo_Salary')
    EXEC sys.sp_cdc_disable_table
        @source_schema = N'dbo',
        @source_name = N'Salary',
        @capture_instance = N'dbo_Salary';
GO

SELECT capture_instance FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dbo.Salary');
GO
