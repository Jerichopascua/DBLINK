-- Run on: CBMS database.
-- Converts dbo.SyncRunHistory.DurationMs (INT, milliseconds) to DurationSeconds
-- (FLOAT, seconds) — matches CBMSB2BLink.Core.Models.SyncRunResult.DurationSeconds
-- and CBMSB2BLink.Data.SqlSyncRunHistoryRepository's updated schema/insert.
--
-- Preserves existing rows' values (converted, not discarded): adds the new column,
-- backfills it from the old one (/1000.0), then drops the old column.

IF COL_LENGTH('dbo.SyncRunHistory', 'DurationMs') IS NOT NULL
   AND COL_LENGTH('dbo.SyncRunHistory', 'DurationSeconds') IS NULL
BEGIN
    ALTER TABLE dbo.SyncRunHistory ADD DurationSeconds FLOAT NULL;
END
GO

IF COL_LENGTH('dbo.SyncRunHistory', 'DurationMs') IS NOT NULL
BEGIN
    UPDATE dbo.SyncRunHistory
    SET DurationSeconds = DurationMs / 1000.0
    WHERE DurationMs IS NOT NULL;
END
GO

IF COL_LENGTH('dbo.SyncRunHistory', 'DurationMs') IS NOT NULL
BEGIN
    ALTER TABLE dbo.SyncRunHistory DROP COLUMN DurationMs;
END
GO

SELECT TOP 5 RunId, SyncKey, Status, DurationSeconds
FROM dbo.SyncRunHistory
ORDER BY RunId DESC;
GO
