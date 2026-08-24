select * from src_tblRetRpt
src_tblCRARawReport


src_tblCRARawReport

-- 1. Parent Table: CRA Raw Report
CREATE TABLE dbo.src_tblCRARawReport (
    RowID                  INT IDENTITY(1,1) NOT NULL,
    Status                 VARCHAR(50) NULL,
    DateResponse           DATETIME NULL,

    CONSTRAINT PK_src_tblCRARawReport PRIMARY KEY CLUSTERED (RowID)
);

-- 2. Child Table: Retail Report
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
    
    -- Field Linkage
    CONSTRAINT FK_src_tblRetRpt_tblCRARawReport 
        FOREIGN KEY (CRARawReportID) 
        REFERENCES dbo.src_tblCRARawReport (RowID)
        ON DELETE SET NULL
        ON UPDATE CASCADE
);



SELECT 
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

UNION ALL

SELECT 
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
  );



--ORIGINAL
SELECT 
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
FROM tblRetRpt a WITH (NOLOCK)
WHERE a.DtlRowID > 0
  AND a.CCRIS_Status_Detailed = 'OK'
  AND a.RowID = (
      SELECT TOP 1 b.RowID
      FROM tblRetRpt b WITH (NOLOCK)
      WHERE b.RefNo = a.RefNo
        AND b.DtlRowID > 0
        AND b.CCRIS_Status_Detailed = 'OK'
        AND b.Date_Response_Detailed >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
  )
  AND a.Date_Response_Detailed >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
  AND a.Date_Response_Detailed < CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE(), 106))

UNION ALL

SELECT 
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
FROM tblRetRpt a WITH (NOLOCK) 
INNER JOIN tblCRARawReport b WITH (NOLOCK) 
    ON a.CRARawReportID = b.RowID 
WHERE b.Status = 'OK'
  AND a.RowID = (
      SELECT TOP 1 c.RowID
      FROM tblRetRpt c WITH (NOLOCK)
      INNER JOIN tblCRARawReport d WITH (NOLOCK) 
          ON c.CRARawReportID = d.RowID
      WHERE c.RefNo = a.RefNo
        AND d.Status = 'OK'
        AND d.DateResponse >= CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE()-1, 106))
        AND d.DateResponse < CONVERT(DATETIME, CONVERT(VARCHAR, GETDATE(), 106))
  );




  CREATE TABLE dbo.BCB_NEW2 (
    BCB_CMS_No             INT NULL,
    BCB_IdNo1              VARCHAR(50) NULL,
    BCB_IdNo2              VARCHAR(50) NULL,
    BCB_Name1              VARCHAR(255) NULL,
    BCB_DOB                VARCHAR(10) NULL,        -- Formatted as DD/MM/YYYY
    BCB_Nationality        VARCHAR(10) NULL,        -- e.g., 'MY'
    BCB_CreateDate         DATETIME NULL,           -- e.g., '2015-01-28 15:57:30.980'
    BCB_LastUpdateBy       VARCHAR(20) NULL,        -- e.g., 'BATCH'
    BCB_ENTKEY             VARCHAR(20) NULL,        -- e.g., '00004156547'
    BCB_RefNo              VARCHAR(20) NULL,        -- e.g., '0000019634'
    BCB_SCR_Scored_TxnCode VARCHAR(10) NULL,        -- e.g., 'USC' / 'SCP'
    BCB_STATUS             VARCHAR(50) NULL,
    BCB_CMS_Status         VARCHAR(50) NULL
);
