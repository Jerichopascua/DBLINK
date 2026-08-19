-- Run on: CBMS database
-- Creates the watermark table, the append-only run-history/audit table (also the
-- source table for a future monitoring dashboard, see docs/ARCHITECTURE.md), and the
-- table type used by CBMSB2BLink.Data.SqlDestinationRepository to insert into BCB_NEW
-- via a table-valued parameter (so the generated CMS_NO range can be captured via
-- OUTPUT INSERTED.CMS_NO in the same statement/transaction as the SyncControl update).
--
-- Assumes dbo.BCB_NEW (CMS_NO IDENTITY PK, IDNO, CREATEDATE, AMOUNT) already exists per
-- StartPrompt.md. Adjust IDNO/AMOUNT precision below to match the real column types.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncControl' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SyncControl
    (
        SyncKey          VARCHAR(50)   NOT NULL PRIMARY KEY,
        LastRowId        BIGINT        NOT NULL CONSTRAINT DF_SyncControl_LastRowId DEFAULT (0),
        LastCmsNo        BIGINT        NULL,
        LastSyncStartUtc DATETIME2     NULL,
        LastSyncEndUtc   DATETIME2     NULL,
        LastSyncStatus   VARCHAR(20)   NULL,
        UpdatedAtUtc     DATETIME2     NOT NULL CONSTRAINT DF_SyncControl_UpdatedAtUtc DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncRunHistory' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SyncRunHistory
    (
        RunId           BIGINT IDENTITY(1,1) PRIMARY KEY,
        SyncKey         VARCHAR(50)   NOT NULL,
        StartedUtc      DATETIME2     NOT NULL,
        CompletedUtc    DATETIME2     NULL,
        Status          VARCHAR(20)   NOT NULL,   -- Success / NoNewData / Failed
        SourceRowIdFrom BIGINT        NULL,
        SourceRowIdTo   BIGINT        NULL,
        CmsNoFrom       BIGINT        NULL,
        CmsNoTo         BIGINT        NULL,
        RecordsRead     INT           NOT NULL CONSTRAINT DF_SyncRunHistory_RecordsRead DEFAULT (0),
        RecordsInserted INT           NOT NULL CONSTRAINT DF_SyncRunHistory_RecordsInserted DEFAULT (0),
        ErrorMessage    NVARCHAR(MAX) NULL,
        HostMachine     VARCHAR(100)  NULL,
        DurationMs      INT           NULL
    );

    CREATE INDEX IX_SyncRunHistory_SyncKey_StartedUtc ON dbo.SyncRunHistory (SyncKey, StartedUtc DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE is_table_type = 1 AND name = 'BcbRecordTableType' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TYPE dbo.BcbRecordTableType AS TABLE
    (
        IdNo       VARCHAR(50)     NOT NULL,
        CreateDate DATETIME2       NOT NULL,
        Amount     DECIMAL(18, 2)  NOT NULL
    );
END
GO
