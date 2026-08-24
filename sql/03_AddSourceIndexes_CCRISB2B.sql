-- Run on: CCRISB2B database.
-- Supporting indexes for usp_GetBCBNewData (sql/02_usp_GetBCBNewData_CCRISB2B.sql).
-- Additive only — does not alter the proc. src_tblRetRpt/src_tblCRARawReport
-- (sql/source_CCRISB2B_01.sql) previously had no index beyond their clustered PK on
-- RowID, so every call to the proc paid for scan-heavy correlated subqueries/joins on
-- RefNo, CRARawReportID, Status, and the response-date filters — cost that's paid once
-- per proc call regardless of @BatchSize, so a smaller @BatchSize means paying it more
-- often for the same day's data.

-- Speeds the outer WHERE on src_tblRetRpt (CCRIS_Status_Detailed = 'OK' AND
-- Date_Response_Detailed in range), used by both the Direct branch and as the shape of
-- "candidate rows" the correlated subqueries iterate.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_src_tblRetRpt_CCRISStatus_DateResponse' AND object_id = OBJECT_ID('dbo.src_tblRetRpt'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_src_tblRetRpt_CCRISStatus_DateResponse
        ON dbo.src_tblRetRpt (CCRIS_Status_Detailed, Date_Response_Detailed)
        INCLUDE (DtlRowID, RefNo);
END
GO

-- Speeds the Direct branch's correlated "TOP 1 ... WHERE b.RefNo = a.RefNo" subquery —
-- previously a scan per outer row.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_src_tblRetRpt_RefNo_Status_DateResponse' AND object_id = OBJECT_ID('dbo.src_tblRetRpt'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_src_tblRetRpt_RefNo_Status_DateResponse
        ON dbo.src_tblRetRpt (RefNo, CCRIS_Status_Detailed, Date_Response_Detailed)
        INCLUDE (DtlRowID);
END
GO

-- Speeds the FKJoined branch's "INNER JOIN src_tblCRARawReport ON a.CRARawReportID = b.RowID".
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_src_tblRetRpt_CRARawReportID' AND object_id = OBJECT_ID('dbo.src_tblRetRpt'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_src_tblRetRpt_CRARawReportID
        ON dbo.src_tblRetRpt (CRARawReportID)
        INCLUDE (RefNo, DtlRowID);
END
GO

-- Speeds src_tblCRARawReport's Status/DateResponse filter, used by both the outer
-- FKJoined join (b.Status = 'OK') and its correlated subquery (d.Status, d.DateResponse).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_src_tblCRARawReport_Status_DateResponse' AND object_id = OBJECT_ID('dbo.src_tblCRARawReport'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_src_tblCRARawReport_Status_DateResponse
        ON dbo.src_tblCRARawReport (Status, DateResponse);
END
GO
