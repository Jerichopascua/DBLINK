-- LOCAL/DEV TESTING ONLY — do not run against real CCRISB2B.
-- Run on: a scratch CCRISB2B database (see docs/TESTING.md).
--
-- Creates dbo.src_tblCRARawReport / dbo.src_tblRetRpt if missing (schema matches
-- sql/source_CCRISB2B_01.sql), then wipes and reseeds both tables with 800,000 rows
-- each, as matched parent/child pairs:
--   - 100,000 pairs dated 2026-08-22 (DateResponse / Date_Imported / Date_Response_Detailed)
--   - 700,000 pairs dated 2026-08-23
-- Every src_tblRetRpt row's CRARawReportID is set to the RowID of its own dedicated
-- src_tblCRARawReport row (both use IDENTITY_INSERT with the same generated row number,
-- so CRARawReportID = RowID is guaranteed by construction, not by identity-ordering
-- assumptions). Both get Status/CCRIS_Status_Detailed = 'OK', so this data exercises
-- BOTH branches of the extract query in sql/source_CCRISB2B_01.sql — the direct
-- CCRIS_Status_Detailed/Date_Response_Detailed branch, and the FK-joined
-- CRARawReportID -> src_tblCRARawReport.Status/DateResponse branch.
--
-- Uses a set-based cascading CROSS JOIN number generator staged into a #Dated temp
-- table, then inserted into both tables — a row-by-row loop at this volume takes
-- minutes to hours.
--
-- dbo.CbmsB2BLink_SentLog (created by sql/02_usp_GetBCBNewData_CCRISB2B.sql) is not
-- touched by this script — a freshly (re)seeded src_tblRetRpt combined with an empty
-- or not-yet-matching CbmsB2BLink_SentLog means every row here looks "unsent" to
-- usp_GetBCBNewData, which is what end-to-end testing wants.
--
-- src_tblCRARawReport can't be TRUNCATEd (it's the FK's referenced side, so SQL Server
-- blocks TRUNCATE even when the child table is empty) — DELETE is used for both tables
-- instead, for a consistent reset.
--
-- Usage: edit @Day1Count/@Day2Count/@Day1Date/@Day2Date below if you need a different
-- split, then run.

USE CCRISB2B;
GO

IF OBJECT_ID('dbo.src_tblCRARawReport', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.src_tblCRARawReport (
        RowID                  INT IDENTITY(1,1) NOT NULL,
        Status                 VARCHAR(50) NULL,
        DateResponse           DATETIME NULL,

        CONSTRAINT PK_src_tblCRARawReport PRIMARY KEY CLUSTERED (RowID)
    );
END
GO

IF OBJECT_ID('dbo.src_tblRetRpt', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.src_tblRetRpt (
        RowID                  INT IDENTITY(1,1) NOT NULL,
        DtlRowID               INT NULL,
        CRARawReportID         INT NULL,
        RefNo                  VARCHAR(50) NULL,
        Cust_IDNo1             VARCHAR(50) NULL,
        Cust_IDNo2             VARCHAR(50) NULL,
        Cust_Name              VARCHAR(255) NULL,
        Cust_DateBR            DATETIME NULL,
        Cust_Nationality       VARCHAR(50) NULL,
        Date_Imported          DATETIME NULL,
        User_ID                VARCHAR(50) NULL,
        Cust_Entity            VARCHAR(50) NULL,
        CCRIS_Status_Detailed  VARCHAR(50) NULL,
        Date_Response_Detailed DATETIME NULL,

        CONSTRAINT PK_src_tblRetRpt PRIMARY KEY CLUSTERED (RowID),

        CONSTRAINT FK_src_tblRetRpt_tblCRARawReport
            FOREIGN KEY (CRARawReportID)
            REFERENCES dbo.src_tblCRARawReport (RowID)
            ON DELETE SET NULL
            ON UPDATE CASCADE
    );
END
GO

DELETE FROM dbo.src_tblRetRpt;
DELETE FROM dbo.src_tblCRARawReport;
GO

IF OBJECT_ID('tempdb..#Dated') IS NOT NULL DROP TABLE #Dated;

DECLARE @Day1Count INT = 100000;                 -- pairs dated @Day1Date
DECLARE @Day2Count INT = 700000;                 -- pairs dated @Day2Date
DECLARE @Day1Date  DATETIME = '2026-08-22';
DECLARE @Day2Date  DATETIME = '2026-08-23';

;WITH L0 AS (SELECT 1 AS c UNION ALL SELECT 1),
L1 AS (SELECT 1 AS c FROM L0 A CROSS JOIN L0 B),   -- 4
L2 AS (SELECT 1 AS c FROM L1 A CROSS JOIN L1 B),   -- 16
L3 AS (SELECT 1 AS c FROM L2 A CROSS JOIN L2 B),   -- 256
L4 AS (SELECT 1 AS c FROM L3 A CROSS JOIN L3 B),   -- 65,536
L5 AS (SELECT 1 AS c FROM L4 A CROSS JOIN L4 B),   -- 4,294,967,296 (capped by TOP below)
Nums AS (
    SELECT TOP (@Day1Count + @Day2Count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM L5
)
SELECT
    n,
    CASE WHEN n <= @Day1Count
         THEN DATEADD(SECOND, n % 86400, @Day1Date)
         ELSE DATEADD(SECOND, n % 86400, @Day2Date)
    END AS d
INTO #Dated
FROM Nums
OPTION (MAXRECURSION 0);

SET IDENTITY_INSERT dbo.src_tblCRARawReport ON;

INSERT INTO dbo.src_tblCRARawReport (RowID, Status, DateResponse)
SELECT n, 'OK', d
FROM #Dated;

SET IDENTITY_INSERT dbo.src_tblCRARawReport OFF;

SET IDENTITY_INSERT dbo.src_tblRetRpt ON;

INSERT INTO dbo.src_tblRetRpt
    (RowID, DtlRowID, CRARawReportID, RefNo, Cust_IDNo1, Cust_IDNo2, Cust_Name, Cust_DateBR,
     Cust_Nationality, Date_Imported, User_ID, Cust_Entity, CCRIS_Status_Detailed,
     Date_Response_Detailed)
SELECT
    n,
    n,
    n,                                                      -- CRARawReportID = matching src_tblCRARawReport.RowID
    CONCAT('REF', RIGHT('0000000000' + CAST(n AS VARCHAR(10)), 10)),
    CONCAT('ID', RIGHT('00000000' + CAST(n AS VARCHAR(10)), 8)),
    NULL,
    CONCAT('Test Customer ', n),
    DATEADD(DAY, -(10950 + (n % 18250)), '2026-08-23'),     -- birthdates ~30-80 years back
    CASE WHEN n % 5 = 0 THEN 'FOREIGN' ELSE 'MY' END,
    d,
    'system',
    RIGHT('000' + CAST(1 + (n % 5) AS VARCHAR(3)), 3),
    'OK',
    d
FROM #Dated;

SET IDENTITY_INSERT dbo.src_tblRetRpt OFF;

DROP TABLE #Dated;
GO

SELECT
    (SELECT COUNT(*) FROM dbo.src_tblCRARawReport) AS ParentRows,
    (SELECT COUNT(*) FROM dbo.src_tblRetRpt) AS ChildRows,
    (SELECT COUNT(*)
     FROM dbo.src_tblRetRpt c
     INNER JOIN dbo.src_tblCRARawReport d ON c.CRARawReportID = d.RowID) AS MatchedPairs,
    SUM(CASE WHEN CAST(Date_Response_Detailed AS DATE) = '2026-08-22' THEN 1 ELSE 0 END) AS Aug22Rows,
    SUM(CASE WHEN CAST(Date_Response_Detailed AS DATE) = '2026-08-23' THEN 1 ELSE 0 END) AS Aug23Rows
FROM dbo.src_tblRetRpt;
GO
