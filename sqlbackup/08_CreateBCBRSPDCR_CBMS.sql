-- Run on: CBMS database.
-- Target table for the BCB_RSP_DCR job (Sync:Jobs in appsettings.json), fed by
-- CCRISB2B's usp_GetBCB_RSP_DCR_Data. Column order matches that job's Target.Columns
-- list exactly — CBMSB2BLink.Data.SqlDestinationRepository maps the source DataTable
-- to this table POSITIONALLY (bulkCopy.ColumnMappings.Add(i, targetColumns[i])), not
-- by name, so column order here must stay in lockstep with appsettings.json.
--
-- Types below match what usp_GetBCB_RSP_DCR_Data actually returns (both UNION ALL
-- branches, joining src_tblDtlRpt_Header and src_tblCRARawReport respectively):
--   CRDCR_RSP_CMS_NO      <- a.RowID (INT)
--   CRDCR_RSP_ENTRYDATE   <- Date_Response_Detailed / DateResponse (DATETIME)
--   CRDCR_RSP_MODE        <- CCRIS_Status / Status (VARCHAR(50))
--   CRDCR_RSP_ERROR       <- CCRIS_Error / ErrorMessage (VARCHAR(MAX))
--   CRDCR_RSP_WARNING     <- CCRIS_Warning / B2BErrorMessage (VARCHAR(MAX))
--   CRDCR_RSP_FRMYR       <- FromYear (INT)
--   CRDCR_RSP_TOYR        <- ToYear (INT)
--   CRDCR_RSP_MOM1..12    <- M1..M12 (VARCHAR(50) each)
--   CRDCR_RSP_LIMTOT      <- CONVERT(VARCHAR(25), LimTot / TotalLimit)
--   CRDCR_RSP_BALTOT      <- CONVERT(VARCHAR(25), BalTot / TotalOutstanding)
--   CRDCR_RSP_SpecialName <- SpecialName (VARCHAR(255))

IF OBJECT_ID('dbo.BCB_RSP_DCR', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BCB_RSP_DCR (
        CRDCR_RSP_CMS_NO      INT           NULL,
        CRDCR_RSP_ENTRYDATE   DATETIME      NULL,
        CRDCR_RSP_MODE        VARCHAR(50)   NULL,
        CRDCR_RSP_ERROR       VARCHAR(MAX)  NULL,
        CRDCR_RSP_WARNING     VARCHAR(MAX)  NULL,
        CRDCR_RSP_FRMYR       INT           NULL,
        CRDCR_RSP_TOYR        INT           NULL,
        CRDCR_RSP_MOM1        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM2        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM3        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM4        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM5        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM6        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM7        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM8        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM9        VARCHAR(50)   NULL,
        CRDCR_RSP_MOM10       VARCHAR(50)   NULL,
        CRDCR_RSP_MOM11       VARCHAR(50)   NULL,
        CRDCR_RSP_MOM12       VARCHAR(50)   NULL,
        CRDCR_RSP_LIMTOT      VARCHAR(25)   NULL,
        CRDCR_RSP_BALTOT      VARCHAR(25)   NULL,
        CRDCR_RSP_SpecialName VARCHAR(255)  NULL
    );
END
GO
