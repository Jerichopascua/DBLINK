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
--   BCB_CMS_No (column 0) is both the key (= src_tblRetRpt.RowID, used for
--   @LastRowId paging) and the first column copied into BCB_NEW2 — never returned as
--   a separate RowID column, since the engine's field-count check requires exactly
--   Target.Columns.Count columns back.
--   @LastRowId defaults to 0 (not 1) so RowID = 1 isn't permanently excluded by the
--   RowID > @LastRowId filters below.
--
-- Dedup/resume tracking: this proc has NO dedup table of its own any more (the
-- earlier dbo.CbmsB2BLink_SentLog mark-on-read table was dropped from CCRISB2B).
-- @LastRowId — pushed down into the WHERE of both branches below, not applied only at
-- the end — is now the ONLY thing preventing a row from being returned twice. See
-- docs/ARCHITECTURE.md, "CBMS-side resume watermark" for what this means for
-- CBMSB2BLink's dbo.CbmsB2BLink_ResumeCursor watermark and the accepted risk that
-- comes with it (a row whose eligibility becomes true only after a higher RowID has
-- already been synced is skipped forever).
--
-- Two known shape differences from a plain "one row per key" contract, both accepted
-- as-is here (not CBMSB2BLink's concern to fix):
--   - No dedup between the two branches: a row matching BOTH the Direct
--     (CCRIS_Status_Detailed/Date_Response_Detailed) and FKJoined
--     (src_tblCRARawReport.Status/DateResponse) conditions is returned TWICE (UNION
--     ALL, no ROW_NUMBER/dedup step).
--   - TOP (@BatchSize) applies to EACH branch independently, not to the combined
--     UNION ALL result — so one call can return up to 2 * @BatchSize rows, not a hard
--     cap of @BatchSize.

CREATE OR ALTER PROCEDURE dbo.usp_GetBCBNewData
    @LastRowId BIGINT = 0,
    @BatchSize INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@BatchSize)
        a.RowID                                                     AS BCB_CMS_No,
        a.Cust_IDNo1                                                AS BCB_IdNo1,
        a.Cust_IDNo2                                                AS BCB_IdNo2,
        a.Cust_Name                                                 AS BCB_Name1,
        CONVERT(VARCHAR(10), a.Cust_DateBR, 103)                   AS BCB_DOB,
        a.Cust_Nationality                                          AS BCB_Nationality,
        a.Date_Imported                                             AS BCB_CreateDate,
        CONVERT(VARCHAR(10), a.[User_ID])                          AS BCB_LastUpdateBy,
        CONVERT(VARCHAR(16), a.Cust_Entity)                         AS BCB_ENTKEY,
        a.RefNo                                                     AS BCB_RefNo,
        'SCP'                                                       AS BCB_SCR_Scored_TxnCode
    FROM src_tblRetRpt a WITH (NOLOCK)
    WHERE a.DtlRowID > 0
      AND a.CCRIS_Status_Detailed = 'OK'
      AND a.RowID = (
          SELECT TOP 1 b.RowID
          FROM src_tblRetRpt b WITH (NOLOCK)
          WHERE b.RefNo = a.RefNo
            AND b.DtlRowID > 0
            AND b.CCRIS_Status_Detailed = 'OK'
            AND b.Date_Response_Detailed >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
      )
      AND a.Date_Response_Detailed >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
      AND a.Date_Response_Detailed < CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE(), 106))
      AND a.RowID > @LastRowId

    UNION ALL

    SELECT TOP (@BatchSize)
        a.RowID                                                     AS BCB_CMS_No,
        a.Cust_IDNo1                                                AS BCB_IdNo1,
        a.Cust_IDNo2                                                AS BCB_IdNo2,
        a.Cust_Name                                                 AS BCB_Name1,
        CONVERT(VARCHAR(10), a.Cust_DateBR, 103)                   AS BCB_DOB,
        a.Cust_Nationality                                          AS BCB_Nationality,
        a.Date_Imported                                             AS BCB_CreateDate,
        CONVERT(VARCHAR(10), a.[User_ID])                          AS BCB_LastUpdateBy,
        CONVERT(VARCHAR(16), a.Cust_Entity)                         AS BCB_ENTKEY,
        a.RefNo                                                     AS BCB_RefNo,
        'SCP'                                                       AS BCB_SCR_Scored_TxnCode
    FROM src_tblRetRpt a WITH (NOLOCK)
    INNER JOIN src_tblCRARawReport b WITH (NOLOCK)
        ON a.CRARawReportID = b.RowID
    WHERE b.Status = 'OK'
      AND a.RowID = (
          SELECT TOP 1 c.RowID
          FROM src_tblRetRpt c WITH (NOLOCK)
          INNER JOIN src_tblCRARawReport d WITH (NOLOCK)
              ON c.CRARawReportID = d.RowID
          WHERE c.RefNo = a.RefNo
            AND d.Status = 'OK'
            AND d.DateResponse >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
            AND d.DateResponse < CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE(), 106))
      )
      AND a.RowID > @LastRowId
END
GO
