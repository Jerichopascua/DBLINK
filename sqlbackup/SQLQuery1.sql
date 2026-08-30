USE [CCRISB2B]
GO

/****** Object:  StoredProcedure [dbo].[usp_GetBCBNewData]    Script Date: 23/08/2026 9:11:39 pm ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER OFF
GO


CREATE   PROCEDURE [dbo].[usp_GetBCBNewData]
    @LastRowId BIGINT,  -- unused by this test proc; kept only to match SqlSourceRepository's call signature
    @BatchSize INT
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH Batch AS (
        --SELECT TOP (@BatchSize) ROWID, IDNO, CREATEDATE, AMOUNT, Sent
        --FROM dbo.tblRPT
        --WHERE Sent = 0
        --ORDER BY ROWID ASC
    )
    UPDATE Batch
    SET Sent = 1
    OUTPUT INSERTED.ROWID, INSERTED.IDNO, INSERTED.CREATEDATE, INSERTED.AMOUNT;
END

GO




-- SELECT * FROM BCB_NEW2