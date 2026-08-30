-- LOCAL/DEV TESTING ONLY — do not run against real CCRISB2B.
-- Run on: CCRISB2B database.
--
-- Full clean reset of the 3 linked source tables, then reseeds all of them with
-- @NewRowCount fresh linked rows, ALL dated yesterday (relative to when this script
-- runs) — a single-day dataset, unlike the earlier two-day bigdata seed.
--
-- Deletes (and reseeds IDENTITY back to 1 for) src_tblRetRpt, src_tblCRARawReport, and
-- src_tblDtlRpt_Header. Also clears dbo.CbmsB2BLink_SentLog — this MUST happen
-- alongside the IDENTITY reset: SentLog previously marked RowID 100,001-800,000 as
-- already sent, and since new rows restart at RowID 1, leaving SentLog in place would
-- make NOT EXISTS(SentLog) incorrectly exclude almost all the fresh data as
-- "already sent."
--
-- This script does NOT reset dbo.CbmsB2BLink_ResumeCursor — that table lives in CBMS,
-- a separate database. Run sql/07_ResetResumeCursor_CBMS_LocalTesting.sql on CBMS
-- immediately after this, or the pipeline's @LastRowId will still start above the
-- freshly reseeded RowID range and skip all of it.
--
-- Uses the same MERGE + OUTPUT-correlation technique as
-- sql/05_SeedLinkedRecords_CCRISB2B_LocalTesting.sql (plain INSERT...SELECT's OUTPUT
-- can't reference the source row, only inserted.*).
--
-- Usage: edit @NewRowCount / @SeedDate below if you need a different volume or date,
-- then run this script, then sql/07_ResetResumeCursor_CBMS_LocalTesting.sql on CBMS.

USE CCRISB2B;
GO

DELETE FROM dbo.src_tblRetRpt;
DELETE FROM dbo.src_tblCRARawReport;
DELETE FROM dbo.src_tblDtlRpt_Header;
DELETE FROM dbo.CbmsB2BLink_SentLog;

DBCC CHECKIDENT ('dbo.src_tblRetRpt', RESEED, 0);
DBCC CHECKIDENT ('dbo.src_tblCRARawReport', RESEED, 0);
DBCC CHECKIDENT ('dbo.src_tblDtlRpt_Header', RESEED, 0);
GO

DECLARE @NewRowCount INT = 800000;
DECLARE @SeedDate DATETIME = CAST(CONVERT(VARCHAR, DATEADD(DAY, -1, GETDATE()), 106) AS DATETIME);  -- "yesterday" at midnight — matches usp_GetBCBNewData's eligibility window

IF OBJECT_ID('tempdb..#Seed') IS NOT NULL DROP TABLE #Seed;

;WITH L0 AS (SELECT 1 AS c UNION ALL SELECT 1),
L1 AS (SELECT 1 AS c FROM L0 A CROSS JOIN L0 B),   -- 4
L2 AS (SELECT 1 AS c FROM L1 A CROSS JOIN L1 B),   -- 16
L3 AS (SELECT 1 AS c FROM L2 A CROSS JOIN L2 B),   -- 256
L4 AS (SELECT 1 AS c FROM L3 A CROSS JOIN L3 B),   -- 65,536
L5 AS (SELECT 1 AS c FROM L4 A CROSS JOIN L4 B),   -- 4,294,967,296 (capped by TOP below)
Nums AS (
    SELECT TOP (@NewRowCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM L5
)
SELECT n, DATEADD(SECOND, n % 86400, @SeedDate) AS d
INTO #Seed
FROM Nums
OPTION (MAXRECURSION 0);

DECLARE @CraMap TABLE (n INT PRIMARY KEY, CraRowID INT NOT NULL);
DECLARE @DtlMap TABLE (n INT PRIMARY KEY, DtlRowID INT NOT NULL);

MERGE INTO dbo.src_tblCRARawReport AS tgt
USING #Seed AS src
    ON 1 = 0
WHEN NOT MATCHED THEN
    INSERT (Status, DateResponse, ErrorMessage, B2BErrorMessage, FromYear, ToYear,
            M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12,
            TotalLimit, TotalOutstanding, SpecialName)
    VALUES ('OK', src.d, NULL, NULL, 2023, 2026,
            '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0',
            10000 + (src.n % 50000), 5000 + (src.n % 20000), NULL)
OUTPUT src.n, inserted.RowID INTO @CraMap (n, CraRowID);

MERGE INTO dbo.src_tblDtlRpt_Header AS tgt
USING #Seed AS src
    ON 1 = 0
WHEN NOT MATCHED THEN
    INSERT (CCRIS_Status, CCRIS_Error, CCRIS_Warning, FromYear, ToYear,
            M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12,
            LimTot, BalTot, SpecialName)
    VALUES ('OK', NULL, NULL, 2023, 2026,
            '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0',
            10000 + (src.n % 50000), 5000 + (src.n % 20000), NULL)
OUTPUT src.n, inserted.RowID INTO @DtlMap (n, DtlRowID);

INSERT INTO dbo.src_tblRetRpt
    (DtlRowID, CRARawReportID, RefNo, Cust_IDNo1, Cust_IDNo2, Cust_Name, Cust_DateBR,
     Cust_Nationality, Date_Imported, User_ID, Cust_Entity, CCRIS_Status_Detailed,
     Date_Response_Detailed)
SELECT
    dm.DtlRowID,
    cm.CraRowID,
    CONCAT('REF', RIGHT('0000000000' + CAST(src.n AS VARCHAR(10)), 10)),
    CONCAT('ID', RIGHT('00000000' + CAST(src.n AS VARCHAR(10)), 8)),
    NULL,
    CONCAT('Test Customer ', src.n),
    DATEADD(DAY, -(10950 + (src.n % 18250)), @SeedDate),
    CASE WHEN src.n % 5 = 0 THEN 'FOREIGN' ELSE 'MY' END,
    src.d,
    'system',
    RIGHT('000' + CAST(1 + (src.n % 5) AS VARCHAR(3)), 3),
    'OK',
    src.d
FROM #Seed AS src
INNER JOIN @CraMap cm ON cm.n = src.n
INNER JOIN @DtlMap dm ON dm.n = src.n;

DROP TABLE #Seed;
GO

SELECT
    (SELECT COUNT(*) FROM dbo.src_tblRetRpt) AS RetRptTotal,
    (SELECT COUNT(*) FROM dbo.src_tblCRARawReport) AS CRARawReportTotal,
    (SELECT COUNT(*) FROM dbo.src_tblDtlRpt_Header) AS DtlRptHeaderTotal,
    (SELECT COUNT(*) FROM dbo.CbmsB2BLink_SentLog) AS SentLogTotal,
    (SELECT COUNT(*)
     FROM dbo.src_tblRetRpt r
     INNER JOIN dbo.src_tblCRARawReport c ON r.CRARawReportID = c.RowID
     INNER JOIN dbo.src_tblDtlRpt_Header d ON r.DtlRowID = d.RowID) AS FullyLinkedRows;
GO
