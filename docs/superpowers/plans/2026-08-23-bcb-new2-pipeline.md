# BCB_NEW2 Pipeline Cutover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retarget CBMSB2BLink's sync pipeline from CCRISB2B `dbo.tblRPT` → CBMS `dbo.BCB_NEW` to CCRISB2B `dbo.src_tblRetRpt`/`dbo.src_tblCRARawReport` → CBMS `dbo.BCB_NEW2`, as a full cutover.

**Architecture:** `SyncEngine`'s orchestration (lock → pull pages → insert batch → record history → notify on failure) is unchanged — only the record shape flowing through it changes, from 3 columns (`IdNo/CreateDate/Amount`) to 12 `BCB_*` business columns. Dedup/resume tracking moves entirely into a new CCRISB2B-side stored procedure + tracking table; the CBMS insert stays a plain, unfiltered `INSERT`.

**Tech Stack:** .NET 6, Dapper 2.1.35, Microsoft.Data.SqlClient 5.2.2, xUnit + Moq, SQL Server (T-SQL).

**Spec:** `docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md`

## Global Constraints

- Full cutover, not a switchable dual pipeline — old `tblRPT`/`BCB_NEW` scripts are deleted, not deprecated-in-place.
- Dedup lives only in the CCRISB2B-side `usp_GetBCBNewData` (via a new `dbo.CbmsB2BLink_SentLog` tracking table, mark-on-read). The CBMS destination insert never filters for duplicates.
- `src_tblRetRpt`/`src_tblCRARawReport` schema is never altered — no tracking columns added to them.
- `SyncEngine.cs`, `IDestinationRepository`, `ISourceRepository`, `InsertBatchResult`, `SyncRunResult`, `ISyncRunHistoryRepository`, `SqlSyncRunHistoryRepository` are all shape-agnostic and are **not modified** by this plan — verify this remains true; if any task discovers otherwise, stop and reconsider before proceeding.

---

### Task 1: CCRISB2B schema — sent-log table + new `usp_GetBCBNewData`

**Files:**
- Modify: `sql/02_usp_GetBCBNewData_CCRISB2B.sql` (full rewrite)

**Interfaces:**
- Produces: stored proc `dbo.usp_GetBCBNewData(@LastRowId BIGINT, @BatchSize INT)`, returning columns `RowID, BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality, BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode`, ordered by `RowID` ascending, at most `@BatchSize` rows, never repeating a previously-returned `RowID`. Table `dbo.CbmsB2BLink_SentLog(RowID INT PRIMARY KEY, SentUtc DATETIME2)`.

- [ ] **Step 1: Replace the file's contents**

Replace the entire contents of `sql/02_usp_GetBCBNewData_CCRISB2B.sql` with:

```sql
-- Run on: CCRISB2B database.
-- Real stored procedure CBMSB2BLink.Data.SqlSourceRepository calls to page through new
-- src_tblRetRpt rows (schema: sql/source_CCRISB2B_01.sql) and hand them to CBMSB2BLink
-- for insertion into CBMS dbo.BCB_NEW2. Replaces the earlier tblRPT-based version of
-- this proc (see git history) as part of the full cutover documented in
-- docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md.
--
-- Contract expected by SqlSourceRepository.GetNewRecordsAsync:
--   EXEC usp_GetBCBNewData @LastRowId = <bigint>, @BatchSize = <int>
--   Returns columns: RowID, BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB,
--   BCB_Nationality, BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo,
--   BCB_SCR_Scored_TxnCode — ordered by RowID ascending, at most @BatchSize rows.
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
    INSERT INTO dbo.CbmsB2BLink_SentLog (RowID, SentUtc)
    OUTPUT
        inserted.RowID,
        b.BCB_CMS_No, b.BCB_IdNo1, b.BCB_IdNo2, b.BCB_Name1, b.BCB_DOB, b.BCB_Nationality,
        b.BCB_CreateDate, b.BCB_LastUpdateBy, b.BCB_ENTKEY, b.BCB_RefNo, b.BCB_SCR_Scored_TxnCode
    SELECT b.RowID, SYSUTCDATETIME()
    FROM Batch b;
END
GO
```

- [ ] **Step 2: Run it against the local scratch CCRISB2B database**

Run: `sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\02_usp_GetBCBNewData_CCRISB2B.sql"`
Expected: no errors; `Changed database context` line absent (script has no `USE`, runs against whatever `-d` selects — pass `-d CCRISB2B` if the default login database isn't CCRISB2B).

- [ ] **Step 3: Verify dedup + pagination behavior manually**

Run (against CCRISB2B, with `src_tblRetRpt`/`src_tblCRARawReport` already seeded per `sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql` from prior work in this session):

```sql
EXEC dbo.usp_GetBCBNewData @LastRowId = 0, @BatchSize = 10;
SELECT COUNT(*) AS SentLogRows FROM dbo.CbmsB2BLink_SentLog;
EXEC dbo.usp_GetBCBNewData @LastRowId = 0, @BatchSize = 10;
```

Expected: first `EXEC` returns up to 10 rows and `SentLogRows` shows the same count; the second identical `EXEC` returns **10 different rows** (not the same ones) because the first batch is now in `CbmsB2BLink_SentLog`.

- [ ] **Step 4: Commit**

```bash
git add sql/02_usp_GetBCBNewData_CCRISB2B.sql
git commit -m "Rewrite usp_GetBCBNewData for src_tblRetRpt/src_tblCRARawReport with sent-log dedup"
```

---

### Task 2: CBMS schema — `BCB_NEW2` surrogate key + new `BcbRecordTableType`

**Files:**
- Modify: `sql/01_CreateSyncRunHistory_CBMS.sql`

**Interfaces:**
- Produces: TVP `dbo.BcbRecordTableType(BCB_CMS_No INT NOT NULL, BCB_IdNo1 VARCHAR(50) NULL, BCB_IdNo2 VARCHAR(50) NULL, BCB_Name1 VARCHAR(255) NULL, BCB_DOB VARCHAR(10) NULL, BCB_Nationality VARCHAR(10) NULL, BCB_CreateDate DATETIME NULL, BCB_LastUpdateBy VARCHAR(20) NULL, BCB_ENTKEY VARCHAR(20) NULL, BCB_RefNo VARCHAR(20) NULL, BCB_SCR_Scored_TxnCode VARCHAR(10) NULL)`. `dbo.BCB_NEW2` gains `Id BIGINT IDENTITY(1,1) PRIMARY KEY`.

- [ ] **Step 1: Replace the file's contents**

Replace the entire contents of `sql/01_CreateSyncRunHistory_CBMS.sql` with:

```sql
-- Run on: CBMS database
-- Creates the append-only run-history/audit table (also the source table for the
-- monitoring dashboard, see docs/ARCHITECTURE.md), adds a surrogate PK to
-- dbo.BCB_NEW2, and (re)creates the table type used by
-- CBMSB2BLink.Data.SqlDestinationRepository to insert into BCB_NEW2 via a table-valued
-- parameter.
--
-- No watermark table here (deliberately) — CBMSB2BLink does not track a resume point
-- in CBMS. The source-side stored procedure (CCRISB2B) is responsible for knowing
-- what's already been sent; SyncRunHistory is audit-only, not read back to decide what
-- to pull next.
--
-- Assumes dbo.BCB_NEW2 (BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB,
-- BCB_Nationality, BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo,
-- BCB_SCR_Scored_TxnCode, BCB_STATUS, BCB_CMS_Status) already exists — this script only
-- adds a surrogate PK to it (skipped if the table doesn't exist yet; re-run this script
-- after creating BCB_NEW2 if it runs first on a fresh database).

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncRunHistory' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SyncRunHistory
    (
        RunId           BIGINT IDENTITY(1,1) PRIMARY KEY,
        SyncKey         VARCHAR(50)   NOT NULL,
        StartedUtc      DATETIME2     NOT NULL,
        CompletedUtc    DATETIME2     NULL,
        Status          VARCHAR(20)   NOT NULL,   -- Success / NoNewData / Failed
        SourceRowIdFrom BIGINT        NULL,
        SourceRowIdTo   BIGINT        NULL,
        CmsNoFrom       BIGINT        NULL,
        CmsNoTo         BIGINT        NULL,
        RecordsRead     INT           NOT NULL CONSTRAINT DF_SyncRunHistory_RecordsRead DEFAULT (0),
        RecordsInserted INT           NOT NULL CONSTRAINT DF_SyncRunHistory_RecordsInserted DEFAULT (0),
        ErrorMessage    NVARCHAR(MAX) NULL,
        HostMachine     VARCHAR(100)  NULL,
        DurationMs      INT           NULL
    );

    CREATE INDEX IX_SyncRunHistory_SyncKey_StartedUtc ON dbo.SyncRunHistory (SyncKey, StartedUtc DESC);
END
GO

IF OBJECT_ID('dbo.BCB_NEW2', 'U') IS NOT NULL AND COL_LENGTH('dbo.BCB_NEW2', 'Id') IS NULL
BEGIN
    ALTER TABLE dbo.BCB_NEW2 ADD Id BIGINT IDENTITY(1,1) NOT NULL;
    ALTER TABLE dbo.BCB_NEW2 ADD CONSTRAINT PK_BCB_NEW2 PRIMARY KEY CLUSTERED (Id);
END
GO

IF EXISTS (SELECT 1 FROM sys.types WHERE is_table_type = 1 AND name = 'BcbRecordTableType' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    DROP TYPE dbo.BcbRecordTableType;
END
GO

CREATE TYPE dbo.BcbRecordTableType AS TABLE
(
    BCB_CMS_No             INT             NOT NULL,
    BCB_IdNo1               VARCHAR(50)     NULL,
    BCB_IdNo2               VARCHAR(50)     NULL,
    BCB_Name1                VARCHAR(255)    NULL,
    BCB_DOB                 VARCHAR(10)     NULL,
    BCB_Nationality          VARCHAR(10)     NULL,
    BCB_CreateDate           DATETIME        NULL,
    BCB_LastUpdateBy         VARCHAR(20)     NULL,
    BCB_ENTKEY               VARCHAR(20)     NULL,
    BCB_RefNo                VARCHAR(20)     NULL,
    BCB_SCR_Scored_TxnCode   VARCHAR(10)     NULL
);
GO
```

- [ ] **Step 2: Run it against the local scratch CBMS database**

Run: `sqlcmd -S ".\SQLEXPRESS" -E -C -d CBMS -i "sql\01_CreateSyncRunHistory_CBMS.sql"`
Expected: no errors.

- [ ] **Step 3: Verify the schema changes landed**

```sql
SELECT COL_LENGTH('dbo.BCB_NEW2', 'Id') AS IdColumnLength;   -- expect non-null (8, for BIGINT)
SELECT name FROM sys.columns WHERE object_id = TYPE_ID('dbo.BcbRecordTableType');  -- expect the 11 BCB_* names
```

- [ ] **Step 4: Commit**

```bash
git add sql/01_CreateSyncRunHistory_CBMS.sql
git commit -m "Add BCB_NEW2 surrogate key and retarget BcbRecordTableType to the new column shape"
```

---

### Task 3: C# pipeline shape swap

This task changes `BcbRecord`, both repository implementations, and their tests together — the solution will not compile until every file in this task is done, so there is one build/test checkpoint at the end rather than one per file.

**Files:**
- Modify: `src/CBMSB2BLink.Core/Models/BcbRecord.cs`
- Modify: `src/CBMSB2BLink.Data/SqlSourceRepository.cs`
- Modify: `src/CBMSB2BLink.Data/SqlDestinationRepository.cs`
- Modify: `src/CBMSB2BLink.Tests/SyncEngineTests.cs`
- Modify: `src/CBMSB2BLink.Tests/HttpSourceRepositoryTests.cs`

**Interfaces:**
- Consumes: `ISourceRepository.GetNewRecordsAsync(long lastRowId, int batchSize, CancellationToken) : Task<IReadOnlyList<BcbRecord>>`, `IDestinationRepository.InsertBatchAsync(ICbmsUnitOfWork, IReadOnlyList<BcbRecord>, CancellationToken) : Task<InsertBatchResult>` — unchanged signatures from `src/CBMSB2BLink.Core/Abstractions/ISourceRepository.cs` and `IDestinationRepository.cs`.
- Produces: `BcbRecord { long RowId; int BcbCmsNo; string? BcbIdNo1; string? BcbIdNo2; string? BcbName1; string? BcbDob; string? BcbNationality; DateTime? BcbCreateDate; string? BcbLastUpdateBy; string? BcbEntKey; string? BcbRefNo; string? BcbScrScoredTxnCode; }`. `RowId` is the pagination/audit key (always equals the source `RowID`); `BcbCmsNo` is the same value carried as the business `BCB_CMS_No` column — both exist because `RowId` is `long` (matching `SyncRunResult.SourceRowIdFrom/To`) while `BcbCmsNo` is `int` (matching the `BCB_NEW2.BCB_CMS_No` column type).

- [ ] **Step 1: Replace `BcbRecord.cs`**

```csharp
using System;

namespace CBMSB2BLink.Core.Models;

/// <summary>
/// One row as returned by CCRISB2B's usp_GetBCBNewData (source: src_tblRetRpt /
/// src_tblCRARawReport, see sql/source_CCRISB2B_01.sql).
/// </summary>
public sealed class BcbRecord
{
    public long RowId { get; init; }
    public int BcbCmsNo { get; init; }
    public string? BcbIdNo1 { get; init; }
    public string? BcbIdNo2 { get; init; }
    public string? BcbName1 { get; init; }
    public string? BcbDob { get; init; }
    public string? BcbNationality { get; init; }
    public DateTime? BcbCreateDate { get; init; }
    public string? BcbLastUpdateBy { get; init; }
    public string? BcbEntKey { get; init; }
    public string? BcbRefNo { get; init; }
    public string? BcbScrScoredTxnCode { get; init; }
}
```

- [ ] **Step 2: Replace `SqlSourceRepository.cs`**

```csharp
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Data;

/// <summary>
/// Direct-SQL implementation of ISourceRepository: EXEC usp_GetBCBNewData against
/// CCRISB2B. A future HttpSourceRepository (the source-side fallback bridge, see
/// docs/ARCHITECTURE.md) implements the same interface for when this connection is
/// unreachable.
/// </summary>
public sealed class SqlSourceRepository : ISourceRepository
{
    private sealed class BcbRecordRow
    {
        public long ROWID { get; init; }
        public int BCB_CMS_No { get; init; }
        public string? BCB_IdNo1 { get; init; }
        public string? BCB_IdNo2 { get; init; }
        public string? BCB_Name1 { get; init; }
        public string? BCB_DOB { get; init; }
        public string? BCB_Nationality { get; init; }
        public System.DateTime? BCB_CreateDate { get; init; }
        public string? BCB_LastUpdateBy { get; init; }
        public string? BCB_ENTKEY { get; init; }
        public string? BCB_RefNo { get; init; }
        public string? BCB_SCR_Scored_TxnCode { get; init; }
    }

    private readonly string _connectionString;
    private readonly SyncOptions _syncOptions;

    public SqlSourceRepository(IOptions<ConnectionStringsOptions> connectionStrings, IOptions<SyncOptions> syncOptions)
    {
        _connectionString = connectionStrings.Value.CcrisB2B;
        _syncOptions = syncOptions.Value;
    }

    public async Task<IReadOnlyList<BcbRecord>> GetNewRecordsAsync(long lastRowId, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            _syncOptions.StoredProcedureName,
            new { LastRowId = lastRowId, BatchSize = batchSize },
            commandType: CommandType.StoredProcedure,
            commandTimeout: _syncOptions.CommandTimeoutSeconds,
            cancellationToken: cancellationToken);

        var rows = await connection.QueryAsync<BcbRecordRow>(command);

        return rows
            .Select(r => new BcbRecord
            {
                RowId = r.ROWID,
                BcbCmsNo = r.BCB_CMS_No,
                BcbIdNo1 = r.BCB_IdNo1,
                BcbIdNo2 = r.BCB_IdNo2,
                BcbName1 = r.BCB_Name1,
                BcbDob = r.BCB_DOB,
                BcbNationality = r.BCB_Nationality,
                BcbCreateDate = r.BCB_CreateDate,
                BcbLastUpdateBy = r.BCB_LastUpdateBy,
                BcbEntKey = r.BCB_ENTKEY,
                BcbRefNo = r.BCB_RefNo,
                BcbScrScoredTxnCode = r.BCB_SCR_Scored_TxnCode
            })
            .OrderBy(r => r.RowId)
            .ToList();
    }
}
```

- [ ] **Step 3: Replace `SqlDestinationRepository.cs`**

```csharp
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Data;

/// <summary>
/// Inserts a batch into CBMS dbo.BCB_NEW2 via a table-valued parameter
/// (dbo.BcbRecordTableType, see sql/01_CreateSyncRunHistory_CBMS.sql). No dedup filter
/// here — the CCRISB2B-side usp_GetBCBNewData is responsible for never returning an
/// already-sent row (see docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md).
/// </summary>
public sealed class SqlDestinationRepository : IDestinationRepository
{
    private readonly int _commandTimeoutSeconds;

    public SqlDestinationRepository(IOptions<SyncOptions> syncOptions)
    {
        _commandTimeoutSeconds = syncOptions.Value.CommandTimeoutSeconds;
    }

    public async Task<InsertBatchResult> InsertBatchAsync(ICbmsUnitOfWork unitOfWork, IReadOnlyList<BcbRecord> records, CancellationToken cancellationToken)
    {
        var uow = (CbmsUnitOfWork)unitOfWork;

        var table = new DataTable();
        table.Columns.Add("BCB_CMS_No", typeof(int));
        table.Columns.Add("BCB_IdNo1", typeof(string));
        table.Columns.Add("BCB_IdNo2", typeof(string));
        table.Columns.Add("BCB_Name1", typeof(string));
        table.Columns.Add("BCB_DOB", typeof(string));
        table.Columns.Add("BCB_Nationality", typeof(string));
        table.Columns.Add("BCB_CreateDate", typeof(System.DateTime));
        table.Columns.Add("BCB_LastUpdateBy", typeof(string));
        table.Columns.Add("BCB_ENTKEY", typeof(string));
        table.Columns.Add("BCB_RefNo", typeof(string));
        table.Columns.Add("BCB_SCR_Scored_TxnCode", typeof(string));

        foreach (var record in records)
        {
            table.Rows.Add(
                record.BcbCmsNo,
                record.BcbIdNo1,
                record.BcbIdNo2,
                record.BcbName1,
                record.BcbDob,
                record.BcbNationality,
                record.BcbCreateDate,
                record.BcbLastUpdateBy,
                record.BcbEntKey,
                record.BcbRefNo,
                record.BcbScrScoredTxnCode);
        }

        var command = uow.Connection.CreateCommand();
        command.Transaction = (SqlTransaction)unitOfWork.Transaction;
        command.CommandTimeout = _commandTimeoutSeconds;
        command.CommandText = @"
INSERT INTO dbo.BCB_NEW2
    (BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
     BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode)
OUTPUT INSERTED.BCB_CMS_No
SELECT BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
       BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode
FROM @Records;";

        var tvp = command.Parameters.AddWithValue("@Records", table);
        tvp.SqlDbType = SqlDbType.Structured;
        tvp.TypeName = "dbo.BcbRecordTableType";

        long? min = null;
        long? max = null;
        var count = 0;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var cmsNo = reader.GetInt32(0);
                count++;
                if (min is null || cmsNo < min) min = cmsNo;
                if (max is null || cmsNo > max) max = cmsNo;
            }
        }

        return new InsertBatchResult
        {
            RecordsInserted = count,
            CmsNoFrom = min,
            CmsNoTo = max
        };
    }
}
```

- [ ] **Step 4: Update `SyncEngineTests.cs`**

Replace the `Record` helper (currently at line 45-51):

```csharp
    private static BcbRecord Record(long rowId) => new()
    {
        RowId = rowId,
        BcbCmsNo = (int)rowId,
        BcbIdNo1 = $"ID{rowId}",
        BcbName1 = $"Test Customer {rowId}",
        BcbCreateDate = DateTime.UtcNow
    };
```

No other lines in this file reference `BcbRecord` field names directly (the rest of the file only asserts on `SyncRunResult`/`InsertBatchResult` fields, which are unchanged) — leave everything else in the file as-is.

- [ ] **Step 5: Update `HttpSourceRepositoryTests.cs`**

Replace the `expected` list in `GetNewRecordsAsync_DeserializesRecords` (currently lines 55-59):

```csharp
        var expected = new List<BcbRecord>
        {
            new() { RowId = 1, BcbCmsNo = 1, BcbIdNo1 = "ID1", BcbCreateDate = new DateTime(2026, 1, 1) },
            new() { RowId = 2, BcbCmsNo = 2, BcbIdNo1 = "ID2", BcbCreateDate = new DateTime(2026, 1, 2) }
        };
```

Replace the two assertions that follow (currently lines 71-72):

```csharp
        Assert.Equal("ID1", result[0].BcbIdNo1);
        Assert.Equal("ID2", result[1].BcbIdNo1);
```

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet test CBMSB2BLink.slnx`
Expected: builds with no errors, all tests pass (same test names/count as before this task — only field usages changed).

- [ ] **Step 7: Commit**

```bash
git add src/CBMSB2BLink.Core/Models/BcbRecord.cs src/CBMSB2BLink.Data/SqlSourceRepository.cs src/CBMSB2BLink.Data/SqlDestinationRepository.cs src/CBMSB2BLink.Tests/SyncEngineTests.cs src/CBMSB2BLink.Tests/HttpSourceRepositoryTests.cs
git commit -m "Swap BcbRecord and both repository implementations to the BCB_NEW2 column shape"
```

---

### Task 4: Retire obsolete dev-seed scripts

**Files:**
- Delete: `sql/dev-seed_CCRISB2B_LocalTesting.sql`
- Delete: `sql/dev-seed-bigdata_CCRISB2B_LocalTesting.sql`
- Delete: `sql/dev-seed_CBMS_LocalTesting.sql`
- Modify: `sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql`

**Interfaces:** none (SQL scripts, no code depends on these).

- [ ] **Step 1: Delete the three obsolete scripts**

```bash
git rm sql/dev-seed_CCRISB2B_LocalTesting.sql sql/dev-seed-bigdata_CCRISB2B_LocalTesting.sql sql/dev-seed_CBMS_LocalTesting.sql
```

These seeded/created `tblRPT` and `BCB_NEW`, which nothing in the app reads or writes to after Task 1-3 land.

- [ ] **Step 2: Add a note to the CRARawReport seed script about `CbmsB2BLink_SentLog`**

In `sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql`, in the header comment block, after the line ending `-- exercises the first (non-FK) branch of the extract query...` add:

```sql
-- dbo.CbmsB2BLink_SentLog (created by sql/02_usp_GetBCBNewData_CCRISB2B.sql) is not
-- touched by this script — a freshly (re)seeded src_tblRetRpt combined with an empty
-- or not-yet-matching CbmsB2BLink_SentLog means every row here looks "unsent" to
-- usp_GetBCBNewData, which is what end-to-end testing wants.
```

- [ ] **Step 3: Verify the remaining seed scripts still run cleanly**

Run, in order:
```powershell
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\dev-seed_BCB_NEW2_CBMS_LocalTesting.sql"
```
Expected: both complete with no errors (same output shape as when they were first run earlier in this project).

- [ ] **Step 4: Commit**

```bash
git add -A sql/
git commit -m "Delete obsolete tblRPT/BCB_NEW dev-seed scripts, note SentLog interaction"
```

---

### Task 5: Update `docs/ARCHITECTURE.md` and `StartPrompt.md`

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `StartPrompt.md`

**Interfaces:** none (documentation only).

> Scope note: `docs/TESTING.md`, `docs/RUNBOOK.md`, `docs/PRODUCTION_SETUP.md`, and `docs/CONFIGURATION.md` also contain `tblRPT`/`BCB_NEW`/3-column-shape references (found via `grep -n "tblRPT\|BCB_NEW\b\|IDNO\|CREATEDATE\|AMOUNT" docs/*.md StartPrompt.md`). Updating those is a larger, lower-risk doc-only follow-up (they're operational runbooks, not load-bearing for the code to work) — deliberately out of scope for this plan. Flag it to the user as a follow-up once this plan is done.

- [ ] **Step 1: Update the Purpose section and diagram in `docs/ARCHITECTURE.md`**

Replace (lines 5-23):

```markdown
CBMSB2BLink is a scheduled .NET 6 Windows console app that copies new rows from
CCRISB2B (`src_tblRetRpt`/`src_tblCRARawReport`) into CBMS (`BCB_NEW2`). It keeps **no
resume watermark of its own** — the source-side stored procedure (`usp_GetBCBNewData`,
owned/managed on the CCRISB2B side) is responsible for knowing what's already been sent
and excluding it. CBMSB2BLink only writes an audit trail of what each run did
(`SyncRunHistory`) — that table is history, not something the app reads back to decide
what to pull next.

```
                ┌───────────────────────┐
                │   CBMSB2BLink.exe      │
                │  (scheduled, one-shot) │
                └──────┬──────────┬──────┘
                       │          │
                 READ  │          │ WRITE (transactional)
                       ▼          ▼
                 CCRISB2B         CBMS
              usp_GetBCBNewData   BCB_NEW2, SyncRunHistory
              (src_tblRetRpt,
               src_tblCRARawReport)
```
```

- [ ] **Step 2: Update the run-flow insert description**

Replace (lines 79-84):

```markdown
   - **Insert**: the full in-memory batch is loaded into an ADO.NET `DataTable` and
     sent as one table-valued parameter (`dbo.BcbRecordTableType`) in a single
     `INSERT INTO dbo.BCB_NEW2 (BCB_CMS_No, BCB_IdNo1, ...) OUTPUT INSERTED.BCB_CMS_No
     SELECT ... FROM @Records` statement — one round trip for the whole batch. Unlike
     the retired `tblRPT`/`BCB_NEW` pipeline, `BCB_CMS_No` is not a server-generated
     identity — it's the source `RowID` copied straight through — so
     `OUTPUT INSERTED.BCB_CMS_No` here just reports the range of values that were
     inserted, for the audit row, rather than capturing generated identities
     (`SqlDestinationRepository.InsertBatchAsync`).
```

- [ ] **Step 3: Update the "No CBMS-side watermark" section's proc reference**

Replace (lines 104-106):

```markdown
on the CCRISB2B side, inside `usp_GetBCBNewData` (or whatever replaces it) —
CBMSB2BLink is now a thin "call the proc, insert what it returns, log it" pipeline with
no opinion about what counts as "new." See "Failure & recovery scenarios" below for
what this means operationally, and `sql/02_usp_GetBCBNewData_CCRISB2B.sql`'s own
comments for the mark-on-read tracking pattern the proc implements via
`dbo.CbmsB2BLink_SentLog`.
```

- [ ] **Step 4: Update the "Where the data lives" section**

Replace (lines 113-126):

```markdown
- CCRISB2B rows come back from Dapper as plain C# objects (`BcbRecordRow` →
  mapped to `BcbRecord`), held in a `List<BcbRecord>` that accumulates **every
  page** pulled during that run (`SyncEngine.PullAllPagesAsync`, `all.AddRange(page)`).
  Nothing is written to disk or handed off between pages.
- At insert time, that same list is copied into an in-memory ADO.NET `DataTable`
  (`SqlDestinationRepository.InsertBatchAsync`) and streamed to SQL Server as one
  table-valued parameter.
- Once the transaction commits (or the process exits), that memory is released —
  nothing about the batch persists on the app side. The only durable record of
  "what happened" is the `SyncRunHistory` row (row counts, `ROWID`/`BCB_CMS_No`
  ranges, duration) — not the row data itself, and not a resume position.

`BcbRecord` holds 12 mostly-short business fields (customer IDs, name, DOB, a few
short codes, one date) — memory pressure in practice is dominated by row *count*, not
row size — see limits below.
```

- [ ] **Step 5: Remove the stale "measured, not just theoretical" benchmark paragraph**

Replace (lines 149-158):

```markdown
**Not yet re-measured under this pipeline**: an earlier version of this app (the
retired `tblRPT`/`BCB_NEW` pipeline) was measured syncing a 500,000-row backlog
end-to-end in 17.5 seconds against a local SQL Server instance. That number no longer
applies to the current `src_tblRetRpt`/`BCB_NEW2` pipeline (different query shape, new
`CbmsB2BLink_SentLog` dedup join) and hasn't been re-measured — treat the batch-size/
duration knobs above as having comfortable headroom based on the old pipeline's
numbers, not as a verified guarantee for this one, until someone re-runs the same kind
of test against `sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql`.
```

- [ ] **Step 6: Update the "Why a TVP" section**

Replace (lines 212-217):

```markdown
`SqlBulkCopy` is the usual choice for bulk inserts, but doing the insert as a single
`INSERT ... SELECT ... FROM @Records` statement lets one round trip carry the whole
batch and still use `OUTPUT` to report back the exact `BCB_CMS_No` range that was
inserted (for the `SyncRunHistory` row), inside the same transaction as the
`SyncRunHistory` write. A table-valued parameter (`dbo.BcbRecordTableType`) is what
makes that single round trip possible for a whole batch of rows rather than one
`INSERT` per row.
```

- [ ] **Step 7: Update the HTTP fallback bridge JSON contract description**

Replace (lines 272-277):

```markdown
- Contract: `GET /api/bcb-new?lastRowId={n}&batchSize={n}` → JSON array of
  `{ rowId, bcbCmsNo, bcbIdNo1, bcbIdNo2, bcbName1, bcbDob, bcbNationality,
  bcbCreateDate, bcbLastUpdateBy, bcbEntKey, bcbRefNo, bcbScrScoredTxnCode }`
  (camelCase), ordered ascending, capped at `batchSize` — same shape
  `usp_GetBCBNewData` returns, whatever its actual filtering logic is (the bridge just
  forwards `lastRowId`/`batchSize` to the same proc call `SqlSourceRepository` makes).
  401 on a missing/wrong `X-Api-Key` header, 400 on invalid query params.
```

- [ ] **Step 8: Update `StartPrompt.md`'s diagram**

Replace the line (currently line 20):
```
                 tblRPT(ROWID,IDNO,CREATEDATE,AMOUNT)     BCB_NEW (CMS_NO,IDNO,CREATEDATE,AMOUNT)
```
with:
```
     src_tblRetRpt/src_tblCRARawReport (see sql/source_CCRISB2B_01.sql)     BCB_NEW2 (BCB_CMS_No, BCB_IdNo1, ... — see sql/01_CreateSyncRunHistory_CBMS.sql)
```

Replace the line (currently line 38):
```
   BCB_NEW
```
with:
```
   BCB_NEW2
```

- [ ] **Step 9: Verify no stale references remain in the two files**

Run: `grep -n "tblRPT\|BCB_NEW\b" docs/ARCHITECTURE.md StartPrompt.md`
Expected: no output (both files should now only mention `BCB_NEW2`, never bare `BCB_NEW`, and never `tblRPT`).

- [ ] **Step 10: Commit**

```bash
git add docs/ARCHITECTURE.md StartPrompt.md
git commit -m "Update ARCHITECTURE.md and StartPrompt.md for the BCB_NEW2 pipeline"
```

---

### Task 6: End-to-end local verification

**Files:** none created/modified — this task only runs things.

**Interfaces:** none.

- [ ] **Step 1: Confirm the console app's local connection strings actually work**

The existing (untracked) `src/CBMSB2BLink.Console/appsettings.json` on this machine uses `User Id=sa;Password=sapassword`. This session's earlier `sqlcmd` calls used Windows auth (`-E`) successfully instead. Test the configured SQL-auth credentials directly:

Run: `sqlcmd -S "JMPASCUADESKTOP\SQLEXPRESS" -U sa -P "sapassword" -C -Q "SELECT 1"`

If this fails, edit `src/CBMSB2BLink.Console/appsettings.json`'s two connection strings to `Integrated Security=true` in place of `User Id=...;Password=...` (Windows auth, matching what's proven to work in this session), e.g.:
`"Server=JMPASCUADESKTOP\\SQLEXPRESS;Database=CCRISB2B;Integrated Security=true;TrustServerCertificate=True;"`

- [ ] **Step 2: Re-run schema + seed scripts fresh, in dependency order**

```powershell
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\02_usp_GetBCBNewData_CCRISB2B.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -d CBMS -i "sql\01_CreateSyncRunHistory_CBMS.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\dev-seed_BCB_NEW2_CBMS_LocalTesting.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -Q "TRUNCATE TABLE CCRISB2B.dbo.CbmsB2BLink_SentLog; TRUNCATE TABLE CBMS.dbo.BCB_NEW2;"
```

- [ ] **Step 3: Build and run the console app once**

```powershell
dotnet build CBMSB2BLink.slnx
dotnet run --project src\CBMSB2BLink.Console
```

Expected: exits 0; log line similar to `Sync succeeded for BCB_NEW2: N records, RowId X-Y, ...`.

- [ ] **Step 4: Verify rows landed and the sent-log is populated**

```sql
SELECT COUNT(*) AS RowsInBcbNew2 FROM CBMS.dbo.BCB_NEW2;
SELECT COUNT(*) AS SentLogRows FROM CCRISB2B.dbo.CbmsB2BLink_SentLog;
```

Expected: both counts equal `N` from Step 3's log line, and equal each other.

- [ ] **Step 5: Run the console app a second time to prove dedup works**

```powershell
dotnet run --project src\CBMSB2BLink.Console
```

Expected: log line `No new records for BCB_NEW2.` — since every row already-seeded in `src_tblRetRpt` is now in `CbmsB2BLink_SentLog` from the first run, and the seed script didn't add new rows in between.

- [ ] **Step 6: Report results to the user**

No commit for this task — it's verification only. Summarize the row counts and pass/fail of each step back to the user.
