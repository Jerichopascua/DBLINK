-- Run on: CBMS database
-- One-time setup for dbo.BCB_NEW2: adds a surrogate BIGINT identity PK (Id), since
-- BCB_CMS_No is a plain copied-over source RowID, not a generated identity, and the
-- table has no other PK. Skipped if the table doesn't exist yet or already has it.
--
-- dbo.SyncRunHistory is NOT created by this script anymore — every job's target
-- database gets it auto-created in code (SqlSyncRunHistoryRepository.EnsureSchemaAsync)
-- the first time that job runs, since jobs can target any database, not just CBMS.
-- dbo.BcbRecordTableType no longer exists at all — the destination insert is
-- SqlBulkCopy now, not a table-valued parameter (see
-- docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md).

IF OBJECT_ID('dbo.BCB_NEW2', 'U') IS NOT NULL AND COL_LENGTH('dbo.BCB_NEW2', 'Id') IS NULL
BEGIN
    ALTER TABLE dbo.BCB_NEW2 ADD Id BIGINT IDENTITY(1,1) NOT NULL;
    ALTER TABLE dbo.BCB_NEW2 ADD CONSTRAINT PK_BCB_NEW2 PRIMARY KEY CLUSTERED (Id);
END
GO
