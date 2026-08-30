-- LOCAL/DEV TESTING ONLY — do not run against real CBMS.
-- Run on: a scratch CBMS-shaped database (see docs/TESTING.md).
--
-- Creates dbo.BCB_NEW2 if missing, then wipes and reseeds it with 2,000 random dummy
-- rows shaped like the output columns of the extract query in
-- sql/source_CCRISB2B_01.sql, with BCB_CreateDate randomized across every day of 2026
-- (not just one or two dates, unlike the CCRISB2B seed scripts).
--
-- BCB_STATUS / BCB_CMS_Status have no example values in the DDL comments, so this
-- picks a small placeholder set (NEW/SENT/FAILED, PENDING/SUCCESS/ERROR) — adjust the
-- @Statuses/@CmsStatuses lists below if the real domain values differ.

USE CBMS;
GO

IF OBJECT_ID('dbo.BCB_NEW2', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BCB_NEW2 (
        BCB_CMS_No             INT NULL,
        BCB_IdNo1               VARCHAR(50) NULL,
        BCB_IdNo2               VARCHAR(50) NULL,
        BCB_Name1                VARCHAR(255) NULL,
        BCB_DOB                 VARCHAR(10) NULL,        -- Formatted as DD/MM/YYYY
        BCB_Nationality          VARCHAR(10) NULL,        -- e.g., 'MY'
        BCB_CreateDate           DATETIME NULL,           -- e.g., '2015-01-28 15:57:30.980'
        BCB_LastUpdateBy         VARCHAR(20) NULL,        -- e.g., 'BATCH'
        BCB_ENTKEY               VARCHAR(20) NULL,        -- e.g., '00004156547'
        BCB_RefNo                VARCHAR(20) NULL,        -- e.g., '0000019634'
        BCB_SCR_Scored_TxnCode   VARCHAR(10) NULL,        -- e.g., 'USC' / 'SCP'
        BCB_STATUS               VARCHAR(50) NULL,
        BCB_CMS_Status           VARCHAR(50) NULL
    );
END
GO

TRUNCATE TABLE dbo.BCB_NEW2;
GO

DECLARE @RowCount INT = 2000;
DECLARE @i INT = 1;

WHILE @i <= @RowCount
BEGIN
    DECLARE @DayOffset INT = ABS(CHECKSUM(NEWID())) % 365;         -- any day in 2026 (2026 is not a leap year)
    DECLARE @MsOffset  INT = ABS(CHECKSUM(NEWID())) % 86400000;    -- random time-of-day, millisecond precision
    DECLARE @CreateDate DATETIME = DATEADD(MILLISECOND, @MsOffset, DATEADD(DAY, @DayOffset, '2026-01-01'));
    DECLARE @DobYearsAgo INT = 18 + (ABS(CHECKSUM(NEWID())) % 62);  -- age 18-79
    DECLARE @Dob DATETIME = DATEADD(DAY, -(ABS(CHECKSUM(NEWID())) % 365), DATEADD(YEAR, -@DobYearsAgo, '2026-01-01'));

    INSERT INTO dbo.BCB_NEW2
        (BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
         BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode,
         BCB_STATUS, BCB_CMS_Status)
    VALUES (
        @i,
        RIGHT('000000000000' + CAST(ABS(CHECKSUM(NEWID())) % 999999999999 AS VARCHAR(12)), 12),
        CASE WHEN @i % 4 = 0
             THEN CONCAT('A', RIGHT('00000000' + CAST(ABS(CHECKSUM(NEWID())) % 99999999 AS VARCHAR(8)), 8))
             ELSE NULL END,
        CONCAT('Test Customer ', @i),
        CONVERT(VARCHAR(10), @Dob, 103),
        CASE (@i % 10) WHEN 0 THEN 'SG' WHEN 1 THEN 'ID' WHEN 2 THEN 'PH' ELSE 'MY' END,
        @CreateDate,
        'BATCH',
        RIGHT('00000000000' + CAST(ABS(CHECKSUM(NEWID())) % 99999999999 AS VARCHAR(11)), 11),
        RIGHT('0000000000' + CAST(@i AS VARCHAR(10)), 10),
        CASE WHEN @i % 2 = 0 THEN 'SCP' ELSE 'USC' END,
        CASE (@i % 3) WHEN 0 THEN 'NEW' WHEN 1 THEN 'SENT' ELSE 'FAILED' END,
        CASE (@i % 3) WHEN 0 THEN 'PENDING' WHEN 1 THEN 'SUCCESS' ELSE 'ERROR' END
    );

    SET @i += 1;
END
GO

SELECT
    COUNT(*) AS TotalRows,
    COUNT(DISTINCT CAST(BCB_CreateDate AS DATE)) AS DistinctDates,
    MIN(BCB_CreateDate) AS MinCreateDate,
    MAX(BCB_CreateDate) AS MaxCreateDate
FROM dbo.BCB_NEW2;
GO
