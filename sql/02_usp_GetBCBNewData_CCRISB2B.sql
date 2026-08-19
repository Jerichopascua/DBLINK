-- Run on: CCRISB2B database
-- Template for the source-side stored procedure CBMSB2BLink.Data.SqlSourceRepository
-- calls to page through new tblRPT rows. Review column names/types against the real
-- tblRPT schema before deploying — this is a starting point, not a verified script.
--
-- Contract expected by SqlSourceRepository.GetNewRecordsAsync:
--   EXEC usp_GetBCBNewData @LastRowId = <bigint>, @BatchSize = <int>
--   Returns columns: ROWID, IDNO, CREATEDATE, AMOUNT — ordered by ROWID ascending,
--   at most @BatchSize rows, all with ROWID > @LastRowId.

CREATE OR ALTER PROCEDURE dbo.usp_GetBCBNewData
    @LastRowId BIGINT,
    @BatchSize INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@BatchSize)
        ROWID,
        IDNO,
        CREATEDATE,
        AMOUNT
    FROM dbo.tblRPT
    WHERE ROWID > @LastRowId
    ORDER BY ROWID ASC;
END
GO
