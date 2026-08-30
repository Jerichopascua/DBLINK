-- LOCAL/DEV TESTING ONLY — do not run against real CCRISB2B.
-- Run on: CCRISB2B database.
--
-- Appends @NewRowCount new src_tblRetRpt rows (continuing after the current MAX(RowID)
-- — nothing existing is touched/deleted), each with its OWN dedicated new row in
-- src_tblCRARawReport (linked via CRARawReportID) and src_tblDtlRpt_Header (linked via
-- DtlRowID) — matching the real production join shape confirmed against tblRetRpt /
-- tblDtlRpt_Header / tblCRARawReport:
--   tblRetRpt.DtlRowID       = tblDtlRpt_Header.RowID
--   tblRetRpt.CRARawReportID = tblCRARawReport.RowID
--
-- All three new rows per set use 'OK'/eligible values and are dated "yesterday"
-- (relative to when this script runs), so they immediately satisfy
-- usp_GetBCBNewData's Direct-branch eligibility window
-- (Date_Response_Detailed in [yesterday 00:00, today 00:00)).
--
-- src_tblCRARawReport and src_tblDtlRpt_Header are currently EMPTY — the existing
-- 800,000 src_tblRetRpt rows link only to the old, renamed src_tblCRARawReport_BAL1
-- (see FK_src_tblRetRpt_tblCRARawReport, which still references _BAL1, not the new
-- src_tblCRARawReport the SP actually joins). New CRARawReportID values generated here
-- happen to satisfy that FK only because src_tblCRARawReport_BAL1 already has matching
-- RowIDs 1.._BAL1's current max — this script doesn't touch or fix that constraint.
--
-- Uses OUTPUT ... INTO to capture each newly-generated identity RowID and correlate it
-- back to its source row, rather than assuming/pre-computing identity values — safe to
-- run repeatedly (each run just appends another @NewRowCount linked set).
--
-- Usage: edit @NewRowCount / @SeedDate below if you need a different volume or date,
-- then run.

USE CCRISB2B;
GO

DECLARE @NewRowCount INT = 1000;
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

-- Plain INSERT...SELECT's OUTPUT clause can only reference inserted.*, not the source
-- row — MERGE is used purely so OUTPUT can correlate each generated identity RowID
-- back to its source row's n (ON 1=0 guarantees every source row is "not matched",
-- i.e. this always behaves as a plain insert of every #Seed row).
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

DECLARE @BaseN INT = (SELECT ISNULL(MAX(RowID), 0) FROM dbo.src_tblRetRpt);

INSERT INTO dbo.src_tblRetRpt
    (DtlRowID, CRARawReportID, RefNo, Cust_IDNo1, Cust_IDNo2, Cust_Name, Cust_DateBR,
     Cust_Nationality, Date_Imported, User_ID, Cust_Entity, CCRIS_Status_Detailed,
     Date_Response_Detailed)
SELECT
    dm.DtlRowID,
    cm.CraRowID,
    CONCAT('REF', RIGHT('0000000000' + CAST(@BaseN + src.n AS VARCHAR(10)), 10)),
    CONCAT('ID', RIGHT('00000000' + CAST(@BaseN + src.n AS VARCHAR(10)), 8)),
    NULL,
    CONCAT('Test Customer ', @BaseN + src.n),
    DATEADD(DAY, -(10950 + ((@BaseN + src.n) % 18250)), @SeedDate),
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
    (SELECT COUNT(*)
     FROM dbo.src_tblRetRpt r
     INNER JOIN dbo.src_tblCRARawReport c ON r.CRARawReportID = c.RowID
     INNER JOIN dbo.src_tblDtlRpt_Header d ON r.DtlRowID = d.RowID) AS FullyLinkedRows;
GO
