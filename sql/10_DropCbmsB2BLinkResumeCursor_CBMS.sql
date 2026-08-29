-- Run on: CBMS database.
-- dbo.CbmsB2BLink_ResumeCursor is no longer used. The resume cursor is now computed
-- fresh every run as MAX(Target.Columns[0]) FROM Target.Table (see
-- CBMSB2BLink.Data.SqlResumeCursorRepository and docs/ARCHITECTURE.md, "CBMS-side
-- resume cursor") instead of being tracked in a separate table — this removes the
-- exact class of bug that caused a duplicate-key insert: the separately-tracked
-- watermark drifting out of sync with what's actually in the target table (e.g. after
-- a manual reset of one side but not the other). Safe to drop; nothing reads from or
-- writes to this table any more.

IF OBJECT_ID('dbo.CbmsB2BLink_ResumeCursor', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.CbmsB2BLink_ResumeCursor;
END
GO
