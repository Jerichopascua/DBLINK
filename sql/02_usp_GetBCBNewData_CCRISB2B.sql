-- Run on: CCRISB2B database.
-- Real stored procedure CBMSB2BLink.Data.SqlSourceRepository calls to page through new
-- src_tblRetRpt rows (schema: sql/source_CCRISB2B_01.sql) and hand them to CBMSB2BLink
-- for insertion into CBMS dbo.BCB_NEW2. Replaces the earlier tblRPT-based version of
-- this proc (see git history) as part of the full cutover documented in
-- docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md.
--
-- Contract expected by the generic sync engine's SqlSourceRepository (see
-- docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md):
--   EXEC usp_GetBCBNewData @LastRowId = <bigint>, @BatchSize = <int>
--   Returns exactly the 11 columns configured in this job's Target.Columns (BCB_NEW2's
--   shape): BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
--   BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode —
--   ordered by BCB_CMS_No ascending, at most @BatchSize rows. BCB_CMS_No (column 0) is
--   both the key (= src_tblRetRpt.RowID, used for @LastRowId paging) and the first
--   column copied into BCB_NEW2 — never returned as a separate RowID column, since the
--   engine's field-count check requires exactly Target.Columns.Count columns back.
--
-- Dedup/resume tracking lives entirely here (CBMSB2BLink keeps no watermark of its
-- own — see docs/ARCHITECTURE.md, "No CBMS-side watermark"): dbo.CbmsB2BLink_SentLog
-- records every RowID this proc has ever returned, and the proc marks a row sent
-- (inserts into CbmsB2BLink_SentLog) in the SAME statement that reads it
-- (mark-on-read). This is a deliberate simplicity-over-at-least-once tradeoff: a
-- CBMS-side failure between this proc call and the CBMS commit means that row is
-- never retried. src_tblRetRpt/src_tblCRARawReport themselves are never modified —
-- the tracking lives in this separate table so the shared business schema stays
-- untouched.

IF OBJECT_ID('dbo.CbmsB2BLink_SentLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CbmsB2BLink_SentLog
    (
        RowID   INT       NOT NULL CONSTRAINT PK_CbmsB2BLink_SentLog PRIMARY KEY,
        SentUtc DATETIME2 NOT NULL
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetBCBNewData
    @LastRowId BIGINT,
    @BatchSize INT
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH Direct AS (
        -- Branch 1: status/response recorded directly on src_tblRetRpt, latest row
        -- per RefNo, responded "yesterday" (matches sql/source_CCRISB2B_01.sql).
        SELECT
            a.RowID,
            1                                                            AS BranchPriority,
            a.RowID                                                      AS BCB_CMS_No,
            a.Cust_IDNo1                                                 AS BCB_IdNo1,
            a.Cust_IDNo2                                                 AS BCB_IdNo2,
            a.Cust_Name                                                  AS BCB_Name1,
            CONVERT(VARCHAR(10), a.Cust_DateBR, 103)                    AS BCB_DOB,
            LEFT(a.Cust_Nationality, 10)                                 AS BCB_Nationality,
            a.Date_Imported                                              AS BCB_CreateDate,
            LEFT(a.[User_ID], 20)                                        AS BCB_LastUpdateBy,
            LEFT(a.Cust_Entity, 20)                                      AS BCB_ENTKEY,
            LEFT(a.RefNo, 20)                                            AS BCB_RefNo,
            'SCP'                                                        AS BCB_SCR_Scored_TxnCode
        FROM dbo.src_tblRetRpt a WITH (NOLOCK)
        WHERE a.DtlRowID > 0
          AND a.CCRIS_Status_Detailed = 'OK'
          AND a.RowID = (
              SELECT TOP 1 b.RowID
              FROM dbo.src_tblRetRpt b WITH (NOLOCK)
              WHERE b.RefNo = a.RefNo
                AND b.DtlRowID > 0
                AND b.CCRIS_Status_Detailed = 'OK'
                AND b.Date_Response_Detailed >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
              ORDER BY b.RowID DESC
          )
          AND a.Date_Response_Detailed >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
          AND a.Date_Response_Detailed < CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE(), 106))
    ),
    FKJoined AS (
        -- Branch 2: status/response recorded on the parent src_tblCRARawReport row.
        SELECT
            a.RowID,
            2                                                            AS BranchPriority,
            a.RowID                                                      AS BCB_CMS_No,
            a.Cust_IDNo1                                                 AS BCB_IdNo1,
            a.Cust_IDNo2                                                 AS BCB_IdNo2,
            a.Cust_Name                                                  AS BCB_Name1,
            CONVERT(VARCHAR(10), a.Cust_DateBR, 103)                    AS BCB_DOB,
            LEFT(a.Cust_Nationality, 10)                                 AS BCB_Nationality,
            a.Date_Imported                                              AS BCB_CreateDate,
            LEFT(a.[User_ID], 20)                                        AS BCB_LastUpdateBy,
            LEFT(a.Cust_Entity, 20)                                      AS BCB_ENTKEY,
            LEFT(a.RefNo, 20)                                            AS BCB_RefNo,
            'USC'                                                        AS BCB_SCR_Scored_TxnCode
        FROM dbo.src_tblRetRpt a WITH (NOLOCK)
        INNER JOIN dbo.src_tblCRARawReport b WITH (NOLOCK)
            ON a.CRARawReportID = b.RowID
        WHERE b.Status = 'OK'
          AND a.RowID = (
              SELECT TOP 1 c.RowID
              FROM dbo.src_tblRetRpt c WITH (NOLOCK)
              INNER JOIN dbo.src_tblCRARawReport d WITH (NOLOCK)
                  ON c.CRARawReportID = d.RowID
              WHERE c.RefNo = a.RefNo
                AND d.Status = 'OK'
                AND d.DateResponse >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
                AND d.DateResponse < CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE(), 106))
              ORDER BY c.RowID DESC
          )
    ),
    Candidates AS (
        SELECT * FROM Direct
        UNION ALL
        SELECT * FROM FKJoined
    ),
    Deduped AS (
        -- A row can satisfy both branches; keep one copy, preferring Direct (branch 1).
        SELECT *, ROW_NUMBER() OVER (PARTITION BY RowID ORDER BY BranchPriority) AS rn
        FROM Candidates
    ),
    Batch AS (
        SELECT TOP (@BatchSize)
            RowID, BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
            BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode
        FROM Deduped
        WHERE rn = 1
          AND RowID > @LastRowId
          AND NOT EXISTS (SELECT 1 FROM dbo.CbmsB2BLink_SentLog s WHERE s.RowID = Deduped.RowID)
        ORDER BY RowID ASC
    )
    MERGE INTO dbo.CbmsB2BLink_SentLog AS tgt
    USING Batch AS b
        ON tgt.RowID = b.RowID
    WHEN NOT MATCHED THEN
        INSERT (RowID, SentUtc) VALUES (b.RowID, SYSUTCDATETIME())
    OUTPUT
        b.BCB_CMS_No, b.BCB_IdNo1, b.BCB_IdNo2, b.BCB_Name1, b.BCB_DOB, b.BCB_Nationality,
        b.BCB_CreateDate, b.BCB_LastUpdateBy, b.BCB_ENTKEY, b.BCB_RefNo, b.BCB_SCR_Scored_TxnCode;
END
GO
