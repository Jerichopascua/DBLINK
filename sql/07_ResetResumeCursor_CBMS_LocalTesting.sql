-- LOCAL/DEV TESTING ONLY — do not run against real CBMS.
-- Run on: CBMS database.
--
-- Resets the BCB_NEW2 job's resume watermark back to 0, so the next run's
-- @LastRowId starts from the beginning again — required after
-- sql/06_ResetAndReseedLinkedRecords_CCRISB2B_LocalTesting.sql resets CCRISB2B's
-- src_tblRetRpt IDENTITY back to 1, otherwise the pipeline would still start above
-- the freshly reseeded RowID range and skip all of it.
--
-- This is exactly the kind of manual reset dbo.CbmsB2BLink_ResumeCursor is designed
-- for (see docs/ARCHITECTURE.md, "CBMS-side resume watermark") — ops updates the
-- table directly, the app just reads whatever value is there.

USE CBMS;
GO

UPDATE dbo.CbmsB2BLink_ResumeCursor
SET LastRowId = 0, DateUpdated = SYSUTCDATETIME()
WHERE JobKey = 'BCB_NEW2';

SELECT * FROM dbo.CbmsB2BLink_ResumeCursor;
GO
