-- Enables change data capture on HrCore and on every captured table.
-- Idempotent. Capture and cleanup jobs run in SQL Server Agent, which must be running.
-- Capture instances are named dbo_<Table>; a table allows at most two.

USE HrCore;
GO

SET NOCOUNT ON;

IF (SELECT is_cdc_enabled FROM sys.databases WHERE name = N'HrCore') = 0
    EXEC sys.sp_cdc_enable_db;
GO

DECLARE @tables TABLE (Name sysname NOT NULL);
INSERT @tables (Name)
VALUES (N'Tenant'), (N'Employee'), (N'Position'), (N'Salary'), (N'DebeziumSignal'), (N'DebeziumHeartbeat');

DECLARE @name sysname, @instance sysname;
DECLARE tables_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @tables;
OPEN tables_cursor;
FETCH NEXT FROM tables_cursor INTO @name;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dbo.' + @name))
    BEGIN
        SET @instance = N'dbo_' + @name;
        EXEC sys.sp_cdc_enable_table
            @source_schema = N'dbo',
            @source_name = @name,
            @role_name = NULL,
            @capture_instance = @instance,
            @supports_net_changes = 0;
    END;

    FETCH NEXT FROM tables_cursor INTO @name;
END;

CLOSE tables_cursor;
DEALLOCATE tables_cursor;
GO

SELECT capture_instance, OBJECT_NAME(source_object_id) AS source_table
FROM cdc.change_tables
ORDER BY source_table;

EXEC sys.sp_cdc_help_jobs;
GO
