-- Run on: CBMS database.
-- Resume watermark, one row per job (JobKey matches Sync:Jobs[].JobKey in
-- appsettings.json, e.g. 'BCB_NEW2'). Read at the start of every run to seed
-- @LastRowId instead of starting at 0, and advanced automatically to the run's
-- SourceRowIdTo on every success (same transaction as the BCB_NEW2 insert and the
-- SyncRunHistory row). Ops can also update it by hand at any time, e.g.:
--   UPDATE dbo.CbmsB2BLink_ResumeCursor SET LastRowId = 123456 WHERE JobKey = 'BCB_NEW2';
-- to force a specific resume point — the app always just reads whatever is there.
--
-- This is a deliberate reintroduction of watermark-style resume state (the earlier
-- dbo.SyncControl table was removed for the same reason this carries: see
-- docs/ARCHITECTURE.md, "CBMS-side resume watermark"). The accepted risk: a row whose
-- eligibility flips "ready" *after* a higher RowID has already been synced will be
-- skipped forever once LastRowId passes it, since the source SP's own
-- NOT EXISTS(CbmsB2BLink_SentLog) check never gets a chance to see it.
--
-- CBMSB2BLink.Data.SqlResumeCursorRepository.EnsureSchemaAsync also creates this table
-- automatically on first run if it doesn't exist — this script is here for manual/DBA
-- deployment, matching the other numbered scripts in this folder.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CbmsB2BLink_ResumeCursor' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CbmsB2BLink_ResumeCursor
    (
        JobKey      VARCHAR(50) NOT NULL CONSTRAINT PK_CbmsB2BLink_ResumeCursor PRIMARY KEY,
        LastRowId   BIGINT      NOT NULL,
        DateUpdated DATETIME2   NOT NULL
    );
END
GO
