-- HrCore source schema: synthetic multi-tenant HR and pay data.
-- Idempotent: creates only what is missing.
--
-- Effective periods are half-open: [EffectiveFrom, EffectiveTo), EffectiveTo NULL = open-ended.
-- Reason, Currency, and MonthlyAmount deliberately have no CHECK constraints: the
-- translator validates them, and the dead-letter demo needs a row that only it rejects.

IF DB_ID(N'HrCore') IS NULL
    CREATE DATABASE HrCore;
GO

-- Debezium snapshots under SNAPSHOT isolation (row versions, no shared locks). Under the
-- default REPEATABLE READ its snapshot transaction kept shared locks on every row it read
-- and blocked all later updates of those rows, including its own heartbeat.
IF (SELECT snapshot_isolation_state FROM sys.databases WHERE name = N'HrCore') = 0
    ALTER DATABASE HrCore SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

USE HrCore;
GO

IF OBJECT_ID(N'dbo.Tenant', N'U') IS NULL
CREATE TABLE dbo.Tenant
(
    TenantId int           NOT NULL CONSTRAINT PK_Tenant PRIMARY KEY,
    Name     nvarchar(100) NOT NULL
);
GO

IF OBJECT_ID(N'dbo.Employee', N'U') IS NULL
CREATE TABLE dbo.Employee
(
    TenantId        int           NOT NULL,
    EmployeeId      int           NOT NULL,
    Name            nvarchar(100) NOT NULL,
    Gender          char(1)       NOT NULL CONSTRAINT CK_Employee_Gender CHECK (Gender IN ('F', 'M', 'X')),
    HireDate        date          NOT NULL,
    TerminationDate date          NULL,
    CONSTRAINT PK_Employee PRIMARY KEY (TenantId, EmployeeId),
    CONSTRAINT FK_Employee_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenant (TenantId)
);
GO

IF OBJECT_ID(N'dbo.Position', N'U') IS NULL
CREATE TABLE dbo.Position
(
    PositionId    int           NOT NULL IDENTITY (1, 1) CONSTRAINT PK_Position PRIMARY KEY,
    TenantId      int           NOT NULL,
    EmployeeId    int           NOT NULL,
    Title         nvarchar(100) NOT NULL,
    Level         tinyint       NOT NULL,
    EffectiveFrom date          NOT NULL,
    EffectiveTo   date          NULL,
    CONSTRAINT FK_Position_Employee FOREIGN KEY (TenantId, EmployeeId) REFERENCES dbo.Employee (TenantId, EmployeeId),
    CONSTRAINT UQ_Position_Employee_From UNIQUE (TenantId, EmployeeId, EffectiveFrom)
);
GO

IF OBJECT_ID(N'dbo.Salary', N'U') IS NULL
CREATE TABLE dbo.Salary
(
    SalaryId      int           NOT NULL IDENTITY (1, 1) CONSTRAINT PK_Salary PRIMARY KEY,
    TenantId      int           NOT NULL,
    EmployeeId    int           NOT NULL,
    MonthlyAmount decimal(12, 2) NOT NULL,
    Currency      char(3)       NOT NULL,
    EffectiveFrom date          NOT NULL,
    EffectiveTo   date          NULL,
    Reason        varchar(20)   NOT NULL,
    CONSTRAINT FK_Salary_Employee FOREIGN KEY (TenantId, EmployeeId) REFERENCES dbo.Employee (TenantId, EmployeeId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Salary_Employee_From' AND object_id = OBJECT_ID(N'dbo.Salary'))
    CREATE INDEX IX_Salary_Employee_From ON dbo.Salary (TenantId, EmployeeId, EffectiveFrom);
GO

-- Debezium signal table: inserting an 'execute-snapshot' row starts an incremental snapshot.
IF OBJECT_ID(N'dbo.DebeziumSignal', N'U') IS NULL
CREATE TABLE dbo.DebeziumSignal
(
    id   varchar(42)   NOT NULL CONSTRAINT PK_DebeziumSignal PRIMARY KEY,
    type varchar(32)   NOT NULL,
    data varchar(2048) NULL
);
GO

-- Updated by the connector's heartbeat query, so the source always produces changes
-- while the connector runs; silence on this table means capture is broken.
IF OBJECT_ID(N'dbo.DebeziumHeartbeat', N'U') IS NULL
CREATE TABLE dbo.DebeziumHeartbeat
(
    Id       int          NOT NULL CONSTRAINT PK_DebeziumHeartbeat PRIMARY KEY,
    LastBeat datetime2(3) NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.DebeziumHeartbeat WHERE Id = 1)
    INSERT dbo.DebeziumHeartbeat (Id, LastBeat) VALUES (1, SYSUTCDATETIME());
GO
