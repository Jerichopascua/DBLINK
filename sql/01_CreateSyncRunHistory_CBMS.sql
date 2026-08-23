-- Run on: CBMS database
-- Creates the append-only run-history/audit table (also the source table for the
-- monitoring dashboard, see docs/ARCHITECTURE.md), adds a surrogate PK to
-- dbo.BCB_NEW2, and (re)creates the table type used by
-- CBMSB2BLink.Data.SqlDestinationRepository to insert into BCB_NEW2 via a table-valued
-- parameter.
--
-- No watermark table here (deliberately) — CBMSB2BLink does not track a resume point
-- in CBMS. The source-side stored procedure (CCRISB2B) is responsible for knowing
-- what's already been sent; SyncRunHistory is audit-only, not read back to decide what
-- to pull next.
--
-- Assumes dbo.BCB_NEW2 (BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB,
-- BCB_Nationality, BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo,
-- BCB_SCR_Scored_TxnCode, BCB_STATUS, BCB_CMS_Status) already exists — this script only
-- adds a surrogate PK to it (skipped if the table doesn't exist yet; re-run this script
-- after creating BCB_NEW2 if it runs first on a fresh database).

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

IF OBJECT_ID('dbo.BCB_NEW2', 'U') IS NOT NULL AND COL_LENGTH('dbo.BCB_NEW2', 'Id') IS NULL
BEGIN
    ALTER TABLE dbo.BCB_NEW2 ADD Id BIGINT IDENTITY(1,1) NOT NULL;
    ALTER TABLE dbo.BCB_NEW2 ADD CONSTRAINT PK_BCB_NEW2 PRIMARY KEY CLUSTERED (Id);
END
GO

IF EXISTS (SELECT 1 FROM sys.types WHERE is_table_type = 1 AND name = 'BcbRecordTableType' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    DROP TYPE dbo.BcbRecordTableType;
END
GO

CREATE TYPE dbo.BcbRecordTableType AS TABLE
(
    BCB_CMS_No             INT             NOT NULL,
    BCB_IdNo1               VARCHAR(50)     NULL,
    BCB_IdNo2               VARCHAR(50)     NULL,
    BCB_Name1                VARCHAR(255)    NULL,
    BCB_DOB                 VARCHAR(10)     NULL,
    BCB_Nationality          VARCHAR(10)     NULL,
    BCB_CreateDate           DATETIME        NULL,
    BCB_LastUpdateBy         VARCHAR(20)     NULL,
    BCB_ENTKEY               VARCHAR(20)     NULL,
    BCB_RefNo                VARCHAR(20)     NULL,
    BCB_SCR_Scored_TxnCode   VARCHAR(10)     NULL
);
GO
