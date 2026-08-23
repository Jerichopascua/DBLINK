# Generic Multi-Job Sync Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generalize CBMSB2BLink from one hardcoded `tblRPT`-shaped pipeline into a config-driven runner for multiple independent source→target sync jobs, each with its own connection strings, using `SqlBulkCopy` instead of a per-pipeline SQL Server table type.

**Architecture:** `SyncEngine` loops over a configured `Sync:Jobs` list, running each job's existing pull-pages/insert/record-history flow in isolation (one job's failure doesn't stop the others). Rows flow as `System.Data.DataTable` end to end instead of a typed model. `BCB_NEW2`'s already-working pipeline becomes the one entry in that list — its SQL (`usp_GetBCBNewData`, `BCB_NEW2`) is untouched.

**Tech Stack:** .NET 6, `Microsoft.Data.SqlClient` (`SqlBulkCopy`), Dapper, xUnit + Moq.

**Spec:** `docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md`

## Global Constraints

- Source SQL still owns pagination and dedup for every job — no watermark added anywhere.
- One process-level file lock covers the whole run (all jobs), not one lock per job.
- Jobs run sequentially and are isolated: one job's failure is recorded and the run continues to the next job. Process exit code is non-zero if any job failed.
- Field-count validation only (source result column count vs. `Target.Columns.Count`) — column *types* are never pre-validated; a type mismatch surfaces as an ordinary `SqlBulkCopy` error.
- `SyncRunHistory` lives in each job's own target DB, auto-created in code (`CREATE TABLE IF NOT EXISTS`) — no manual SQL script required per target DB.
- `SyncRunHistory`'s column shape (`SyncKey`, `SourceRowIdFrom/To`, `CmsNoFrom/To`) is unchanged, so `CBMSB2BLink.Monitoring.Api` needs zero changes. `JobKey` is written into the existing `SyncKey` column.
- `CBMSB2BLink.FallbackBridge.Api`, `src/CBMSB2BLink.Data/HttpSourceRepository.cs`, and everything that only exists to support them are deleted, not stubbed.
- `CBMSB2BLink.Monitoring.Api` is not touched by this plan at all.

---

### Task 1: Core config & model layer

**Files:**
- Create: `src/CBMSB2BLink.Core/Options/SyncJobOptions.cs`
- Modify: `src/CBMSB2BLink.Core/Options/SyncOptions.cs`
- Create: `src/CBMSB2BLink.Core/Options/SyncOptionsValidator.cs`
- Delete: `src/CBMSB2BLink.Core/Options/ConnectionStringsOptions.cs`
- Delete: `src/CBMSB2BLink.Core/Options/BridgeAuthOptions.cs`
- Delete: `src/CBMSB2BLink.Core/Options/FallbackBridgeOptions.cs`
- Delete: `src/CBMSB2BLink.Core/Options/FallbackBridgeOptionsValidator.cs`
- Delete: `src/CBMSB2BLink.Core/Models/BcbRecord.cs`

**Interfaces:**
- Produces: `SourceJobOptions { string ConnectionString; string CommandText; string CommandType; }`, `TargetJobOptions { string ConnectionString; string Table; List<string> Columns; }`, `SyncJobOptions { string JobKey; SourceJobOptions Source; TargetJobOptions Target; int BatchSize; int CommandTimeoutSeconds; }`, `SyncOptions { const string SectionName = "Sync"; int MaxRunDurationSeconds; string? LockFilePath; List<SyncJobOptions> Jobs; }`.

- [ ] **Step 1: Create `SyncJobOptions.cs`**

```csharp
using System.Collections.Generic;

namespace CBMSB2BLink.Core.Options;

public sealed class SourceJobOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Stored procedure name, or raw SQL text when CommandType is "Text".</summary>
    public string CommandText { get; set; } = string.Empty;

    /// <summary>"StoredProcedure" (default) or "Text".</summary>
    public string CommandType { get; set; } = "StoredProcedure";
}

public sealed class TargetJobOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Schema-qualified target table, e.g. "dbo.BCB_NEW2".</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>
    /// Ordered target column names. Columns[0] and the source query's first result
    /// column are always the key (used for @LastRowId paging and the audit row's
    /// from/to range) — see docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md.
    /// </summary>
    public List<string> Columns { get; set; } = new();
}

public sealed class SyncJobOptions
{
    public string JobKey { get; set; } = string.Empty;

    public SourceJobOptions Source { get; set; } = new();

    public TargetJobOptions Target { get; set; } = new();

    public int BatchSize { get; set; } = 5000;

    public int CommandTimeoutSeconds { get; set; } = 120;
}
```

- [ ] **Step 2: Replace `SyncOptions.cs`**

```csharp
using System.Collections.Generic;

namespace CBMSB2BLink.Core.Options;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Whole-run budget across every configured job, not reset per job.</summary>
    public int MaxRunDurationSeconds { get; set; } = 1800;

    /// <summary>
    /// Path to the file lock used to prevent overlapping runs. Defaults to
    /// %ProgramData%\CBMSB2BLink\run.lock when left blank.
    /// </summary>
    public string? LockFilePath { get; set; }

    public List<SyncJobOptions> Jobs { get; set; } = new();
}
```

- [ ] **Step 3: Create `SyncOptionsValidator.cs`**

```csharp
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Core.Options;

/// <summary>
/// SyncOptions.Jobs is a nested collection — the built-in ValidateDataAnnotations()
/// does not recurse into nested objects/collections, so [Required]/[Range] attributes
/// on SyncJobOptions/SourceJobOptions/TargetJobOptions would silently never run.
/// Validation for every job's required fields lives here instead, mirroring the
/// existing FallbackBridgeOptionsValidator pattern in this codebase (now removed —
/// see Task 6 — but this class follows the same shape).
/// </summary>
public sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions options)
    {
        if (options.MaxRunDurationSeconds is < 1 or > 86_400)
        {
            return ValidateOptionsResult.Fail("Sync:MaxRunDurationSeconds must be between 1 and 86400.");
        }

        if (options.Jobs is null || options.Jobs.Count == 0)
        {
            return ValidateOptionsResult.Fail("Sync:Jobs must have at least one job configured.");
        }

        foreach (var job in options.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.JobKey))
            {
                return ValidateOptionsResult.Fail("Sync:Jobs has an entry with an empty JobKey.");
            }

            var prefix = $"Sync:Jobs[{job.JobKey}]";

            if (string.IsNullOrWhiteSpace(job.Source?.ConnectionString))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Source:ConnectionString is required.");
            }

            if (string.IsNullOrWhiteSpace(job.Source.CommandText))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Source:CommandText is required.");
            }

            if (string.IsNullOrWhiteSpace(job.Target?.ConnectionString))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Target:ConnectionString is required.");
            }

            if (string.IsNullOrWhiteSpace(job.Target.Table))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Target:Table is required.");
            }

            if (job.Target.Columns is null || job.Target.Columns.Count == 0)
            {
                return ValidateOptionsResult.Fail($"{prefix}: Target:Columns must have at least one column.");
            }

            if (job.BatchSize is < 1 or > 100_000)
            {
                return ValidateOptionsResult.Fail($"{prefix}: BatchSize must be between 1 and 100000.");
            }

            if (job.CommandTimeoutSeconds is < 1 or > 3600)
            {
                return ValidateOptionsResult.Fail($"{prefix}: CommandTimeoutSeconds must be between 1 and 3600.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
```

- [ ] **Step 4: Delete the four obsolete option/model files**

```bash
rm src/CBMSB2BLink.Core/Options/ConnectionStringsOptions.cs
rm src/CBMSB2BLink.Core/Options/BridgeAuthOptions.cs
rm src/CBMSB2BLink.Core/Options/FallbackBridgeOptions.cs
rm src/CBMSB2BLink.Core/Options/FallbackBridgeOptionsValidator.cs
rm src/CBMSB2BLink.Core/Models/BcbRecord.cs
```

- [ ] **Step 5: Build the Core project alone (will show downstream errors — expected)**

Run: `dotnet build src/CBMSB2BLink.Core/CBMSB2BLink.Core.csproj`
Expected: FAILS — `SyncEngine.cs` still references `BcbRecord`/old `SyncOptions` shape/`ICbmsUnitOfWork`. This is expected; Task 4 fixes `SyncEngine.cs`. Do not fix it in this task — confirm the *only* errors are in `SyncEngine.cs` (nothing else in Core should reference the deleted types yet), then move on.

- [ ] **Step 6: Commit**

```bash
git add src/CBMSB2BLink.Core/Options/SyncJobOptions.cs src/CBMSB2BLink.Core/Options/SyncOptions.cs src/CBMSB2BLink.Core/Options/SyncOptionsValidator.cs
git rm src/CBMSB2BLink.Core/Options/ConnectionStringsOptions.cs src/CBMSB2BLink.Core/Options/BridgeAuthOptions.cs src/CBMSB2BLink.Core/Options/FallbackBridgeOptions.cs src/CBMSB2BLink.Core/Options/FallbackBridgeOptionsValidator.cs src/CBMSB2BLink.Core/Models/BcbRecord.cs
git commit -m "Replace SyncOptions with a multi-job config shape, drop single-pipeline options"
```

---

### Task 2: Core abstractions layer

**Files:**
- Modify: `src/CBMSB2BLink.Core/Abstractions/ICbmsUnitOfWork.cs` (rename to `ITargetUnitOfWork.cs`)
- Modify: `src/CBMSB2BLink.Core/Abstractions/ICbmsUnitOfWorkFactory.cs` (rename to `ITargetUnitOfWorkFactory.cs`)
- Modify: `src/CBMSB2BLink.Core/Abstractions/ISourceRepository.cs`
- Modify: `src/CBMSB2BLink.Core/Abstractions/IDestinationRepository.cs`
- Modify: `src/CBMSB2BLink.Core/Abstractions/ISyncRunHistoryRepository.cs`
- Modify: `src/CBMSB2BLink.Core/Abstractions/INotificationService.cs`

**Interfaces:**
- Consumes: `SourceJobOptions`, `TargetJobOptions` from Task 1.
- Produces: `ITargetUnitOfWork : IAsyncDisposable { DbTransaction Transaction; Task InitializeAsync(CancellationToken); Task CommitAsync(CancellationToken); Task RollbackAsync(CancellationToken); }`, `ITargetUnitOfWorkFactory { ITargetUnitOfWork Create(string connectionString); }`, `ISourceRepository.GetNewRecordsAsync(SourceJobOptions source, long lastRowId, int batchSize, int commandTimeoutSeconds, CancellationToken) : Task<DataTable>`, `IDestinationRepository.InsertBatchAsync(ITargetUnitOfWork unitOfWork, string targetTable, IReadOnlyList<string> targetColumns, DataTable records, CancellationToken) : Task<InsertBatchResult>`, `ISyncRunHistoryRepository.EnsureSchemaAsync(string targetConnectionString, CancellationToken)`, `ISyncRunHistoryRepository.RecordRunAsync(ITargetUnitOfWork, SyncRunResult, CancellationToken)`, `ISyncRunHistoryRepository.RecordFailedRunAsync(string targetConnectionString, SyncRunResult, CancellationToken)`, `INotificationService.SendFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken)`.

- [ ] **Step 1: Delete and recreate the unit-of-work interfaces under new names**

```bash
git mv src/CBMSB2BLink.Core/Abstractions/ICbmsUnitOfWork.cs src/CBMSB2BLink.Core/Abstractions/ITargetUnitOfWork.cs
git mv src/CBMSB2BLink.Core/Abstractions/ICbmsUnitOfWorkFactory.cs src/CBMSB2BLink.Core/Abstractions/ITargetUnitOfWorkFactory.cs
```

Replace `ITargetUnitOfWork.cs`'s contents:

```csharp
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// A single open target-database connection + transaction shared by the destination
/// insert and the SyncRunHistory write for one job's run, so they commit or roll back
/// together — a crash mid-run leaves that job's target DB untouched for that run.
/// </summary>
public interface ITargetUnitOfWork : IAsyncDisposable
{
    DbTransaction Transaction { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
```

Replace `ITargetUnitOfWorkFactory.cs`'s contents:

```csharp
namespace CBMSB2BLink.Core.Abstractions;

public interface ITargetUnitOfWorkFactory
{
    /// <summary>Opens a unit of work against the given job's target connection string.</summary>
    ITargetUnitOfWork Create(string connectionString);
}
```

- [ ] **Step 2: Replace `ISourceRepository.cs`**

```csharp
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Options;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Reads one page of new records from a job's source. Column shape comes entirely
/// from what the job's configured query returns — see
/// docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md.
/// </summary>
public interface ISourceRepository
{
    Task<DataTable> GetNewRecordsAsync(SourceJobOptions source, long lastRowId, int batchSize, int commandTimeoutSeconds, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Replace `IDestinationRepository.cs`**

```csharp
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Bulk-inserts a batch of rows into a job's target table, mapping DataTable columns
/// positionally to targetColumns (source and target column names are never assumed to
/// match).
/// </summary>
public interface IDestinationRepository
{
    Task<InsertBatchResult> InsertBatchAsync(ITargetUnitOfWork unitOfWork, string targetTable, IReadOnlyList<string> targetColumns, DataTable records, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Replace `ISyncRunHistoryRepository.cs`**

```csharp
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Audit-only: appends SyncRunHistory rows in each job's own target database.
/// CBMSB2BLink does not track a resume watermark itself for any job — the source-side
/// query is responsible for knowing what's already been sent.
/// </summary>
public interface ISyncRunHistoryRepository
{
    /// <summary>Creates dbo.SyncRunHistory in the target database if it doesn't already exist.</summary>
    Task EnsureSchemaAsync(string targetConnectionString, CancellationToken cancellationToken);

    /// <summary>Appends a SyncRunHistory row within the given unit of work (Success / NoNewData path).</summary>
    Task RecordRunAsync(ITargetUnitOfWork unitOfWork, SyncRunResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort append of a Failed SyncRunHistory row on its own connection, used after the
    /// write transaction has already been rolled back (or never opened).
    /// </summary>
    Task RecordFailedRunAsync(string targetConnectionString, SyncRunResult result, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Replace `INotificationService.cs`**

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

public interface INotificationService
{
    /// <summary>Sends one aggregate notification listing every job that failed in a run.</summary>
    Task SendFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken cancellationToken);
}
```

- [ ] **Step 6: Build Core alone again**

Run: `dotnet build src/CBMSB2BLink.Core/CBMSB2BLink.Core.csproj`
Expected: still FAILS, only in `SyncEngine.cs` (now with more/different errors — it references the renamed/reshaped interfaces too). Confirm no other file in Core has errors.

- [ ] **Step 7: Commit**

```bash
git add src/CBMSB2BLink.Core/Abstractions/
git commit -m "Generalize source/destination/unit-of-work/notification interfaces for multi-job sync"
```

---

### Task 3: Data layer implementations

**Files:**
- Modify: `src/CBMSB2BLink.Data/CbmsUnitOfWork.cs` (rename to `TargetUnitOfWork.cs`)
- Modify: `src/CBMSB2BLink.Data/CbmsUnitOfWorkFactory.cs` (rename to `TargetUnitOfWorkFactory.cs`)
- Modify: `src/CBMSB2BLink.Data/SqlSourceRepository.cs`
- Modify: `src/CBMSB2BLink.Data/SqlDestinationRepository.cs`
- Modify: `src/CBMSB2BLink.Data/SqlSyncRunHistoryRepository.cs`
- Modify: `src/CBMSB2BLink.Data/ServiceCollectionExtensions.cs`
- Delete: `src/CBMSB2BLink.Data/HttpSourceRepository.cs`

**Interfaces:**
- Consumes: everything from Task 2.
- Produces: `TargetUnitOfWork(string connectionString) : ITargetUnitOfWork` with `internal SqlConnection Connection`, `TargetUnitOfWorkFactory : ITargetUnitOfWorkFactory`, `SqlSourceRepository : ISourceRepository`, `SqlDestinationRepository : IDestinationRepository`, `SqlSyncRunHistoryRepository : ISyncRunHistoryRepository`.

- [ ] **Step 1: Rename and update the unit-of-work implementation**

```bash
git mv src/CBMSB2BLink.Data/CbmsUnitOfWork.cs src/CBMSB2BLink.Data/TargetUnitOfWork.cs
git mv src/CBMSB2BLink.Data/CbmsUnitOfWorkFactory.cs src/CBMSB2BLink.Data/TargetUnitOfWorkFactory.cs
```

Replace `TargetUnitOfWork.cs`'s contents:

```csharp
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

public sealed class TargetUnitOfWork : ITargetUnitOfWork
{
    private readonly string _connectionString;
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private bool _completed;

    public TargetUnitOfWork(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbTransaction Transaction =>
        _transaction ?? throw new InvalidOperationException("Call InitializeAsync before using the transaction.");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken);
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            throw new InvalidOperationException("Call InitializeAsync before committing.");
        }

        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null || _completed)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && _transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch
            {
                // Connection may already be broken; nothing more we can do here.
            }
        }

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    internal SqlConnection Connection =>
        _connection ?? throw new InvalidOperationException("Call InitializeAsync before using the connection.");
}
```

Replace `TargetUnitOfWorkFactory.cs`'s contents:

```csharp
using CBMSB2BLink.Core.Abstractions;

namespace CBMSB2BLink.Data;

public sealed class TargetUnitOfWorkFactory : ITargetUnitOfWorkFactory
{
    public ITargetUnitOfWork Create(string connectionString) => new TargetUnitOfWork(connectionString);
}
```

- [ ] **Step 2: Replace `SqlSourceRepository.cs`**

```csharp
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Options;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

/// <summary>
/// Direct-SQL implementation of ISourceRepository: executes the job's configured
/// Source.CommandText (proc or raw SQL) with @LastRowId/@BatchSize and returns the raw
/// result set as a DataTable — column shape comes entirely from what the query
/// returns, not a hardcoded model.
/// </summary>
public sealed class SqlSourceRepository : ISourceRepository
{
    public async Task<DataTable> GetNewRecordsAsync(SourceJobOptions source, long lastRowId, int batchSize, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(source.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = source.CommandText;
        command.CommandType = source.CommandType == "Text" ? CommandType.Text : CommandType.StoredProcedure;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@LastRowId", lastRowId);
        command.Parameters.AddWithValue("@BatchSize", batchSize);

        var table = new DataTable();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        table.Load(reader);
        return table;
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
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

/// <summary>
/// Bulk-inserts a batch into the job's target table via SqlBulkCopy, mapping DataTable
/// columns positionally to targetColumns. No SQL Server table type needed. The
/// returned key range is computed from the DataTable itself (targetColumns[0]/column 0
/// is always the key, copied straight through from the source — never a target-side
/// generated identity), not from a database round trip — see
/// docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md, "Why no
/// OUTPUT-based identity capture".
/// </summary>
public sealed class SqlDestinationRepository : IDestinationRepository
{
    public async Task<InsertBatchResult> InsertBatchAsync(ITargetUnitOfWork unitOfWork, string targetTable, IReadOnlyList<string> targetColumns, DataTable records, CancellationToken cancellationToken)
    {
        var uow = (TargetUnitOfWork)unitOfWork;

        using var bulkCopy = new SqlBulkCopy(uow.Connection, SqlBulkCopyOptions.Default, (SqlTransaction)unitOfWork.Transaction)
        {
            DestinationTableName = targetTable
        };

        for (var i = 0; i < targetColumns.Count; i++)
        {
            bulkCopy.ColumnMappings.Add(i, targetColumns[i]);
        }

        await bulkCopy.WriteToServerAsync(records, cancellationToken);

        long? min = null;
        long? max = null;
        if (records.Rows.Count > 0)
        {
            min = System.Convert.ToInt64(records.Rows[0][0]);
            max = System.Convert.ToInt64(records.Rows[records.Rows.Count - 1][0]);
        }

        return new InsertBatchResult
        {
            RecordsInserted = records.Rows.Count,
            CmsNoFrom = min,
            CmsNoTo = max
        };
    }
}
```

- [ ] **Step 4: Replace `SqlSyncRunHistoryRepository.cs`**

```csharp
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

public sealed class SqlSyncRunHistoryRepository : ISyncRunHistoryRepository
{
    public async Task EnsureSchemaAsync(string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(EnsureSchemaSql, cancellationToken: cancellationToken));
    }

    public async Task RecordRunAsync(ITargetUnitOfWork unitOfWork, SyncRunResult result, CancellationToken cancellationToken)
    {
        var uow = (TargetUnitOfWork)unitOfWork;

        var command = new CommandDefinition(InsertRunHistorySql, ToParams(result), transaction: uow.Transaction, cancellationToken: cancellationToken);
        await uow.Connection.ExecuteAsync(command);
    }

    public async Task RecordFailedRunAsync(string targetConnectionString, SyncRunResult result, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(InsertRunHistorySql, ToParams(result), cancellationToken: cancellationToken));
    }

    private const string EnsureSchemaSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncRunHistory' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SyncRunHistory
    (
        RunId           BIGINT IDENTITY(1,1) PRIMARY KEY,
        SyncKey         VARCHAR(50)   NOT NULL,
        StartedUtc      DATETIME2     NOT NULL,
        CompletedUtc    DATETIME2     NULL,
        Status          VARCHAR(20)   NOT NULL,
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
END";

    private const string InsertRunHistorySql = @"
INSERT INTO dbo.SyncRunHistory
    (SyncKey, StartedUtc, CompletedUtc, Status, SourceRowIdFrom, SourceRowIdTo,
     CmsNoFrom, CmsNoTo, RecordsRead, RecordsInserted, ErrorMessage, HostMachine, DurationMs)
VALUES
    (@SyncKey, @StartedUtc, @CompletedUtc, @Status, @SourceRowIdFrom, @SourceRowIdTo,
     @CmsNoFrom, @CmsNoTo, @RecordsRead, @RecordsInserted, @ErrorMessage, @HostMachine, @DurationMs);";

    private static object ToParams(SyncRunResult result) => new
    {
        result.SyncKey,
        result.StartedUtc,
        result.CompletedUtc,
        Status = result.Status.ToString(),
        result.SourceRowIdFrom,
        result.SourceRowIdTo,
        result.CmsNoFrom,
        result.CmsNoTo,
        result.RecordsRead,
        result.RecordsInserted,
        result.ErrorMessage,
        result.HostMachine,
        result.DurationMs
    };
}
```

- [ ] **Step 5: Replace `ServiceCollectionExtensions.cs`**

```csharp
using CBMSB2BLink.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CBMSB2BLink.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every CBMSB2BLink.Data piece — there is only one source mode now
    /// (direct SQL), so ISourceRepository is registered here too instead of being
    /// picked by the composition root.
    /// </summary>
    public static IServiceCollection AddCbmsB2BLinkData(this IServiceCollection services)
    {
        services.AddSingleton<ISourceRepository, SqlSourceRepository>();
        services.AddSingleton<IDestinationRepository, SqlDestinationRepository>();
        services.AddSingleton<ISyncRunHistoryRepository, SqlSyncRunHistoryRepository>();
        services.AddSingleton<ITargetUnitOfWorkFactory, TargetUnitOfWorkFactory>();
        return services;
    }
}
```

- [ ] **Step 6: Delete `HttpSourceRepository.cs`**

```bash
git rm src/CBMSB2BLink.Data/HttpSourceRepository.cs
```

- [ ] **Step 7: Build the Data project alone**

Run: `dotnet build src/CBMSB2BLink.Data/CBMSB2BLink.Data.csproj`
Expected: succeeds — Data no longer depends on anything Console-side or on `BcbRecord`.

- [ ] **Step 8: Commit**

```bash
git add src/CBMSB2BLink.Data/
git commit -m "Rewrite Data layer for multi-job sync: SqlBulkCopy insert, renamed unit-of-work, delete HttpSourceRepository"
```

---

### Task 4: `SyncEngine` multi-job orchestration + aggregate email

**Files:**
- Modify: `src/CBMSB2BLink.Core/SyncEngine.cs`
- Modify: `src/CBMSB2BLink.Console/Infrastructure/EmailNotificationService.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: `SyncEngine.RunAsync(CancellationToken) : Task<List<SyncRunResult>>` (was `Task<SyncRunResult>` — one result per configured job now).

- [ ] **Step 1: Replace `SyncEngine.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Core;

/// <summary>
/// Orchestrates one CBMSB2BLink run: acquire one process-level lock, then run every
/// configured job in Sync:Jobs, in order, isolated (one job failing does not stop the
/// others). CBMSB2BLink does not track a resume watermark itself for any job — each
/// job's source query is responsible for knowing what's already been sent (see
/// ISourceRepository).
/// </summary>
public sealed class SyncEngine
{
    private readonly ISourceRepository _sourceRepository;
    private readonly IDestinationRepository _destinationRepository;
    private readonly ISyncRunHistoryRepository _syncRunHistoryRepository;
    private readonly ITargetUnitOfWorkFactory _unitOfWorkFactory;
    private readonly INotificationService _notificationService;
    private readonly IRunLock _runLock;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        ISourceRepository sourceRepository,
        IDestinationRepository destinationRepository,
        ISyncRunHistoryRepository syncRunHistoryRepository,
        ITargetUnitOfWorkFactory unitOfWorkFactory,
        INotificationService notificationService,
        IRunLock runLock,
        IOptions<SyncOptions> options,
        ILogger<SyncEngine> logger)
    {
        _sourceRepository = sourceRepository;
        _destinationRepository = destinationRepository;
        _syncRunHistoryRepository = syncRunHistoryRepository;
        _unitOfWorkFactory = unitOfWorkFactory;
        _notificationService = notificationService;
        _runLock = runLock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<SyncRunResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<SyncRunResult>();

        using var heldLock = await _runLock.TryAcquireAsync(cancellationToken);
        if (heldLock is null)
        {
            _logger.LogWarning("Another CBMSB2BLink run already holds the lock. Exiting without action.");
            results.Add(new SyncRunResult
            {
                SyncKey = "(lock)",
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
                Status = SyncRunStatus.Failed,
                ErrorMessage = "Skipped: another run is already in progress (lock held)."
            });
            return results;
        }

        foreach (var job in _options.Jobs)
        {
            var result = await RunJobAsync(job, cancellationToken);
            results.Add(result);
        }

        var failed = results.Where(r => r.Status == SyncRunStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            await TryNotifyFailureAsync(failed, cancellationToken);
        }

        return results;
    }

    private async Task<SyncRunResult> RunJobAsync(SyncJobOptions job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new SyncRunResult
        {
            SyncKey = job.JobKey,
            StartedUtc = DateTime.UtcNow
        };

        try
        {
            _logger.LogInformation("Starting sync for {JobKey}", job.JobKey);

            await _syncRunHistoryRepository.EnsureSchemaAsync(job.Target.ConnectionString, cancellationToken);

            var batch = await PullAllPagesAsync(job, cancellationToken);
            result.RecordsRead = batch.Rows.Count;

            if (batch.Rows.Count == 0)
            {
                _logger.LogInformation("No new records for {JobKey}.", job.JobKey);
                result.Status = SyncRunStatus.NoNewData;
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                await using var noopUow = _unitOfWorkFactory.Create(job.Target.ConnectionString);
                await noopUow.InitializeAsync(cancellationToken);
                await _syncRunHistoryRepository.RecordRunAsync(noopUow, result, cancellationToken);
                await noopUow.CommitAsync(cancellationToken);

                return result;
            }

            result.SourceRowIdFrom = Convert.ToInt64(batch.Rows[0][0]);
            result.SourceRowIdTo = Convert.ToInt64(batch.Rows[batch.Rows.Count - 1][0]);

            await using var uow = _unitOfWorkFactory.Create(job.Target.ConnectionString);
            await uow.InitializeAsync(cancellationToken);
            try
            {
                var insertResult = await _destinationRepository.InsertBatchAsync(uow, job.Target.Table, job.Target.Columns, batch, cancellationToken);
                result.RecordsInserted = insertResult.RecordsInserted;
                result.CmsNoFrom = insertResult.CmsNoFrom;
                result.CmsNoTo = insertResult.CmsNoTo;

                result.Status = SyncRunStatus.Success;
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                await _syncRunHistoryRepository.RecordRunAsync(uow, result, cancellationToken);
                await uow.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Sync succeeded for {JobKey}: {RecordsInserted} records, RowId {From}-{To}, {DurationMs}ms",
                    job.JobKey, result.RecordsInserted, result.SourceRowIdFrom, result.SourceRowIdTo, result.DurationMs);

                return result;
            }
            catch
            {
                await uow.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync run failed for {JobKey}", job.JobKey);
            result.Status = SyncRunStatus.Failed;
            result.ErrorMessage = ex.ToString();
            result.CompletedUtc = DateTime.UtcNow;
            result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

            await TryRecordFailedRunAsync(job, result, cancellationToken);
            return result;
        }
    }

    /// <summary>
    /// Pulls every page for one job. The first page's column count is checked against
    /// job.Target.Columns.Count before any further paging or insert happens — a
    /// mismatch fails the job immediately with no partial work (see Global Constraints:
    /// field-count validation only, not type validation).
    /// </summary>
    private async Task<DataTable> PullAllPagesAsync(SyncJobOptions job, CancellationToken cancellationToken)
    {
        DataTable? all = null;
        var cursor = 0L;

        while (true)
        {
            var page = await _sourceRepository.GetNewRecordsAsync(job.Source, cursor, job.BatchSize, job.CommandTimeoutSeconds, cancellationToken);

            if (all is null)
            {
                if (page.Columns.Count != job.Target.Columns.Count)
                {
                    throw new InvalidOperationException(
                        $"Job {job.JobKey}: source query returned {page.Columns.Count} column(s) but Target.Columns configures {job.Target.Columns.Count}. Fix the job's Target.Columns list or the source query.");
                }

                all = page;
            }
            else
            {
                foreach (DataRow row in page.Rows)
                {
                    all.ImportRow(row);
                }
            }

            if (page.Rows.Count == 0)
            {
                break;
            }

            cursor = Convert.ToInt64(page.Rows[page.Rows.Count - 1][0]);

            if (page.Rows.Count < job.BatchSize)
            {
                break;
            }
        }

        return all!;
    }

    private async Task TryRecordFailedRunAsync(SyncJobOptions job, SyncRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _syncRunHistoryRepository.RecordFailedRunAsync(job.Target.ConnectionString, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record failed-run history for {JobKey}", result.SyncKey);
        }
    }

    private async Task TryNotifyFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken cancellationToken)
    {
        try
        {
            await _notificationService.SendFailureAsync(failedResults, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send failure notification for {Count} failed job(s)", failedResults.Count);
        }
    }
}
```

- [ ] **Step 2: Replace `EmailNotificationService.cs`**

```csharp
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CBMSB2BLink.App.Infrastructure;

public sealed class EmailNotificationService : INotificationService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(IOptions<EmailOptions> options, ILogger<EmailNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken cancellationToken)
    {
        if (!_options.EnableOnFailure || _options.To.Length == 0 || failedResults.Count == 0)
        {
            _logger.LogInformation(
                "Failure email suppressed (EnableOnFailure={Enabled}, recipients={Count}, failedJobs={FailedCount}).",
                _options.EnableOnFailure, _options.To.Length, failedResults.Count);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        foreach (var to in _options.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        var jobList = string.Join(", ", failedResults.Select(r => r.SyncKey));
        message.Subject = $"[CBMSB2BLink] Sync FAILED for {failedResults.Count} job(s): {jobList}";

        var body = new StringBuilder();
        body.AppendLine("CBMSB2BLink sync run had one or more failed jobs.");
        body.AppendLine();
        foreach (var result in failedResults)
        {
            body.AppendLine($"JobKey: {result.SyncKey}");
            body.AppendLine($"Host: {result.HostMachine}");
            body.AppendLine($"Started (UTC): {result.StartedUtc:u}");
            body.AppendLine($"Completed (UTC): {result.CompletedUtc:u}");
            body.AppendLine($"RecordsRead: {result.RecordsRead}");
            body.AppendLine($"RecordsInserted: {result.RecordsInserted}");
            body.AppendLine("Error:");
            body.AppendLine(result.ErrorMessage);
            body.AppendLine(new string('-', 40));
        }

        message.Body = new TextPart("plain") { Text = body.ToString() };

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, _options.UseSsl, cancellationToken);

        if (!string.IsNullOrEmpty(_options.SmtpUsername))
        {
            await client.AuthenticateAsync(_options.SmtpUsername, _options.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
```

- [ ] **Step 3: Build Core alone — should now succeed**

Run: `dotnet build src/CBMSB2BLink.Core/CBMSB2BLink.Core.csproj`
Expected: succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/CBMSB2BLink.Core/SyncEngine.cs src/CBMSB2BLink.Console/Infrastructure/EmailNotificationService.cs
git commit -m "Rewrite SyncEngine for per-job isolated multi-job runs, aggregate failure email"
```

---

### Task 5: Console composition root + config

**Files:**
- Modify: `src/CBMSB2BLink.Console/Program.cs`
- Modify: `src/CBMSB2BLink.Console/appsettings.json`
- Modify: `src/CBMSB2BLink.Console/appsettings.Development.json.example`

**Interfaces:**
- Consumes: `SyncEngine.RunAsync(CancellationToken) : Task<List<SyncRunResult>>` from Task 4, `SyncOptionsValidator` from Task 1.

- [ ] **Step 1: Replace `Program.cs`**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using CBMSB2BLink.App.Infrastructure;
using CBMSB2BLink.Core;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using CBMSB2BLink.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace CBMSB2BLink.App;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .UseSerilog((context, services, loggerConfiguration) =>
                loggerConfiguration.ReadFrom.Configuration(context.Configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddOptions<SyncOptions>()
                    .Bind(context.Configuration.GetSection(SyncOptions.SectionName))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>();

                services.AddOptions<EmailOptions>()
                    .Bind(context.Configuration.GetSection(EmailOptions.SectionName));

                services.AddCbmsB2BLinkData();
                services.AddSingleton<IRunLock, FileRunLock>();
                services.AddSingleton<INotificationService, EmailNotificationService>();
                services.AddSingleton<SyncEngine>();
            })
            .Build();

        try
        {
            using var cts = new System.Threading.CancellationTokenSource();
            System.Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            var syncOptions = host.Services.GetRequiredService<IOptions<SyncOptions>>().Value;
            cts.CancelAfter(TimeSpan.FromSeconds(syncOptions.MaxRunDurationSeconds));

            var engine = host.Services.GetRequiredService<SyncEngine>();
            var results = await engine.RunAsync(cts.Token);

            return results.Any(r => r.Status == SyncRunStatus.Failed) ? 1 : 0;
        }
        catch (OptionsValidationException ex)
        {
            Log.Fatal(ex, "Configuration validation failed: {Message}", string.Join("; ", ex.Failures));
            return 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CBMSB2BLink terminated unexpectedly.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
```

- [ ] **Step 2: Replace `appsettings.json`**

Keep the real local connection strings currently in this file (`Server=JMPASCUADESKTOP\SQLEXPRESS;...;User Id=sa;Password=sapassword;...`) — they move from the top-level `ConnectionStrings` section into the one job's `Source`/`Target` blocks. `Email` and `Serilog` sections are copied through unchanged.

```json
{
  "Sync": {
    "MaxRunDurationSeconds": 1800,
    "LockFilePath": "",
    "Jobs": [
      {
        "JobKey": "BCB_NEW2",
        "Source": {
          "ConnectionString": "Server=JMPASCUADESKTOP\\SQLEXPRESS;Database=CCRISB2B;User Id=sa;Password=sapassword;TrustServerCertificate=True;",
          "CommandText": "usp_GetBCBNewData",
          "CommandType": "StoredProcedure"
        },
        "Target": {
          "ConnectionString": "Server=JMPASCUADESKTOP\\SQLEXPRESS;Database=CBMS;User Id=sa;Password=sapassword;TrustServerCertificate=True;",
          "Table": "dbo.BCB_NEW2",
          "Columns": [
            "BCB_CMS_No", "BCB_IdNo1", "BCB_IdNo2", "BCB_Name1", "BCB_DOB",
            "BCB_Nationality", "BCB_CreateDate", "BCB_LastUpdateBy", "BCB_ENTKEY",
            "BCB_RefNo", "BCB_SCR_Scored_TxnCode"
          ]
        },
        "BatchSize": 5000,
        "CommandTimeoutSeconds": 120
      }
    ]
  },
  "Email": {
    "EnableOnFailure": true,
    "SmtpHost": "smtp.bank.local",
    "SmtpPort": 25,
    "UseSsl": false,
    "SmtpUsername": "",
    "SmtpPassword": "",
    "From": "CBMSB2BLink@bank.local",
    "To": [ "cbms-ops@bank.local" ]
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "C:/ProgramData/CBMSB2BLink/logs/log-.txt",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30,
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      }
    ],
    "Enrich": [ "FromLogContext", "WithMachineName" ]
  }
}
```

- [ ] **Step 3: Replace `appsettings.Development.json.example`**

```json
{
  "Sync": {
    "Jobs": [
      {
        "JobKey": "BCB_NEW2",
        "Source": {
          "ConnectionString": "Server=YOUR_SERVER\\SQLEXPRESS;Database=CCRISB2B;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
        },
        "Target": {
          "ConnectionString": "Server=YOUR_SERVER\\SQLEXPRESS;Database=CBMS;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
        }
      }
    ]
  },
  "Email": {
    "EnableOnFailure": false
  }
}
```

- [ ] **Step 4: Build the Console project alone**

Run: `dotnet build src/CBMSB2BLink.Console/CBMSB2BLink.Console.csproj`
Expected: FAILS — `CBMSB2BLink.Console.csproj` still project-references `CBMSB2BLink.FallbackBridge.Api`... actually check: `CBMSB2BLink.Console.csproj` does **not** reference `FallbackBridge.Api` (only `Tests.csproj` does — verified by reading both project files). Expected: **succeeds**, 0 errors. If it fails, the error will name what's still referencing removed types — resolve before continuing (do not guess; read the actual error).

- [ ] **Step 5: Commit**

```bash
git add src/CBMSB2BLink.Console/Program.cs src/CBMSB2BLink.Console/appsettings.json src/CBMSB2BLink.Console/appsettings.Development.json.example
git commit -m "Rewire Console composition root and config for multi-job Sync:Jobs"
```

---

### Task 6: Remove `CBMSB2BLink.FallbackBridge.Api`

**Files:**
- Delete: `src/CBMSB2BLink.FallbackBridge.Api/` (whole directory)
- Delete: `src/CBMSB2BLink.Tests/ApiKeyAuthTests.cs`
- Delete: `src/CBMSB2BLink.Tests/HttpSourceRepositoryTests.cs`
- Modify: `CBMSB2BLink.slnx`
- Modify: `src/CBMSB2BLink.Tests/CBMSB2BLink.Tests.csproj`

**Interfaces:** none — this task only removes things.

- [ ] **Step 1: Delete the FallbackBridge.Api project and its tests**

```bash
git rm -r src/CBMSB2BLink.FallbackBridge.Api
git rm src/CBMSB2BLink.Tests/ApiKeyAuthTests.cs
git rm src/CBMSB2BLink.Tests/HttpSourceRepositoryTests.cs
```

- [ ] **Step 2: Remove the project entry from `CBMSB2BLink.slnx`**

Remove this line from the `<Folder Name="/src/">` block:

```xml
    <Project Path="src/CBMSB2BLink.FallbackBridge.Api/CBMSB2BLink.FallbackBridge.Api.csproj" />
```

- [ ] **Step 3: Remove the project reference from `CBMSB2BLink.Tests.csproj`**

Remove this line from its `<ItemGroup>`:

```xml
    <ProjectReference Include="..\CBMSB2BLink.FallbackBridge.Api\CBMSB2BLink.FallbackBridge.Api.csproj" />
```

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build CBMSB2BLink.slnx`
Expected: FAILS only in `src/CBMSB2BLink.Tests/SyncEngineTests.cs` (still references the old `BcbRecord`/single-result `SyncEngine` shape — Task 7 fixes it). Confirm no other project has errors, and that `CBMSB2BLink.Monitoring.Api` still builds untouched.

- [ ] **Step 5: Commit**

```bash
git add CBMSB2BLink.slnx src/CBMSB2BLink.Tests/CBMSB2BLink.Tests.csproj
git commit -m "Remove CBMSB2BLink.FallbackBridge.Api and its tests (HTTP-fallback mode dropped)"
```

---

### Task 7: Rewrite `SyncEngineTests.cs`

**Files:**
- Modify: `src/CBMSB2BLink.Tests/SyncEngineTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4.

- [ ] **Step 1: Replace `SyncEngineTests.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CBMSB2BLink.Tests;

public class SyncEngineTests
{
    private readonly Mock<ISourceRepository> _source = new();
    private readonly Mock<IDestinationRepository> _destination = new();
    private readonly Mock<ISyncRunHistoryRepository> _syncRunHistory = new();
    private readonly Mock<ITargetUnitOfWorkFactory> _uowFactory = new();
    private readonly Mock<ITargetUnitOfWork> _uow = new();
    private readonly Mock<INotificationService> _notification = new();
    private readonly Mock<IRunLock> _runLock = new();
    private readonly SyncOptions _options = new() { MaxRunDurationSeconds = 60, Jobs = new List<SyncJobOptions> { Job("JOB1") } };

    public SyncEngineTests()
    {
        _runLock.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        _uowFactory.Setup(x => x.Create(It.IsAny<string>())).Returns(_uow.Object);
    }

    private SyncEngine CreateEngine() => new(
        _source.Object,
        _destination.Object,
        _syncRunHistory.Object,
        _uowFactory.Object,
        _notification.Object,
        _runLock.Object,
        Options.Create(_options),
        NullLogger<SyncEngine>.Instance);

    private static SyncJobOptions Job(string jobKey, int batchSize = 10) => new()
    {
        JobKey = jobKey,
        Source = new SourceJobOptions { ConnectionString = "source-cs", CommandText = $"usp_Test_{jobKey}", CommandType = "StoredProcedure" },
        Target = new TargetJobOptions { ConnectionString = "target-cs", Table = "dbo.Target", Columns = new List<string> { "KeyCol", "NameCol" } },
        BatchSize = batchSize,
        CommandTimeoutSeconds = 30
    };

    /// <summary>Builds a page with the correct 2-column shape matching Job()'s Target.Columns.</summary>
    private static DataTable Page(params long[] keys)
    {
        var table = new DataTable();
        table.Columns.Add("Key", typeof(long));
        table.Columns.Add("Name", typeof(string));
        foreach (var key in keys)
        {
            table.Rows.Add(key, $"Row{key}");
        }
        return table;
    }

    private void SetupSource(string commandText, DataTable page)
    {
        _source.Setup(x => x.GetNewRecordsAsync(
                It.Is<SourceJobOptions>(s => s.CommandText == commandText),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
    }

    [Fact]
    public async Task RunAsync_HappyPath_RecordsRunAndCommits()
    {
        SetupSource("usp_Test_JOB1", Page(101, 102, 103));
        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 3, CmsNoFrom = 101, CmsNoTo = 103 });

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(SyncRunStatus.Success, results[0].Status);
        Assert.Equal(3, results[0].RecordsInserted);
        Assert.Equal(103, results[0].SourceRowIdTo);
        Assert.Equal(103, results[0].CmsNoTo);

        _syncRunHistory.Verify(x => x.RecordRunAsync(_uow.Object, It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.Success), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<IReadOnlyList<SyncRunResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoNewData_RecordsRunWithoutTouchingDestination()
    {
        SetupSource("usp_Test_JOB1", Page());

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.NoNewData, results[0].Status);
        _destination.Verify(x => x.InsertBatchAsync(It.IsAny<ITargetUnitOfWork>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<CancellationToken>()), Times.Never);
        _syncRunHistory.Verify(x => x.RecordRunAsync(_uow.Object, It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.NoNewData), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_FieldCountMismatch_FailsJobWithoutInserting()
    {
        var wrongShapedPage = new DataTable();
        wrongShapedPage.Columns.Add("OnlyOneColumn", typeof(long));
        wrongShapedPage.Rows.Add(1L);

        SetupSource("usp_Test_JOB1", wrongShapedPage);

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, results[0].Status);
        Assert.Contains("1 column(s)", results[0].ErrorMessage);
        Assert.Contains("configures 2", results[0].ErrorMessage);
        _destination.Verify(x => x.InsertBatchAsync(It.IsAny<ITargetUnitOfWork>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_OneJobFails_OtherJobStillRuns()
    {
        _options.Jobs = new List<SyncJobOptions> { Job("JOB1"), Job("JOB2") };

        _source.Setup(x => x.GetNewRecordsAsync(
                It.Is<SourceJobOptions>(s => s.CommandText == "usp_Test_JOB1"),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("source unreachable"));

        SetupSource("usp_Test_JOB2", Page(201));
        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 1, CmsNoFrom = 201, CmsNoTo = 201 });

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(SyncRunStatus.Failed, results[0].Status);
        Assert.Equal("JOB1", results[0].SyncKey);
        Assert.Equal(SyncRunStatus.Success, results[1].Status);
        Assert.Equal("JOB2", results[1].SyncKey);

        _notification.Verify(x => x.SendFailureAsync(
            It.Is<IReadOnlyList<SyncRunResult>>(list => list.Count == 1 && list[0].SyncKey == "JOB1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_DestinationInsertFails_RollsBackAndRecordsFailure()
    {
        SetupSource("usp_Test_JOB1", Page(101));
        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("insert failed"));

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, results[0].Status);

        _uow.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _syncRunHistory.Verify(x => x.RecordFailedRunAsync("target-cs", It.IsAny<SyncRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<IReadOnlyList<SyncRunResult>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MultiPageBatch_PagesUntilPartialPage()
    {
        _options.Jobs = new List<SyncJobOptions> { Job("JOB1", batchSize: 2) };

        _source.SetupSequence(x => x.GetNewRecordsAsync(
                It.Is<SourceJobOptions>(s => s.CommandText == "usp_Test_JOB1"),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(1, 2))
            .ReturnsAsync(Page(3));

        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.Is<DataTable>(t => t.Rows.Count == 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 3, CmsNoFrom = 1, CmsNoTo = 3 });

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Success, results[0].Status);
        Assert.Equal(3, results[0].RecordsRead);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), 0, 2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), 2, 2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), 3, 2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_LockHeld_ReturnsFailedWithoutRunningAnyJob()
    {
        _runLock.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IDisposable?)null);

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(SyncRunStatus.Failed, results[0].Status);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<IReadOnlyList<SyncRunResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test CBMSB2BLink.slnx`
Expected: builds with 0 errors, all tests pass (7 in `SyncEngineTests`, plus `HealthCalculatorTests` and any other Monitoring.Api-side tests unaffected by this plan).

- [ ] **Step 3: Commit**

```bash
git add src/CBMSB2BLink.Tests/SyncEngineTests.cs
git commit -m "Rewrite SyncEngineTests for multi-job orchestration, field-count validation, job isolation"
```

---

### Task 8: SQL trim, docs, and end-to-end verification

**Files:**
- Modify: `sql/01_CreateSyncRunHistory_CBMS.sql`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `StartPrompt.md`

**Interfaces:** none — SQL/doc updates plus a live verification run.

> Scope note, same as the `BCB_NEW2` cutover plan before this one: `docs/TESTING.md`, `docs/RUNBOOK.md`, `docs/PRODUCTION_SETUP.md`, `docs/CONFIGURATION.md` still describe the single-pipeline `ConnectionStrings`/`Sync:SyncKey` config shape. Updating those is a larger, lower-risk doc-only follow-up — deliberately out of scope here. Flag it to the user once this task is done.

- [ ] **Step 1: Trim `sql/01_CreateSyncRunHistory_CBMS.sql` to just the `BCB_NEW2` surrogate key**

`SyncRunHistory` creation now happens in C# (`SqlSyncRunHistoryRepository.EnsureSchemaAsync`, Task 3) and `dbo.BcbRecordTableType` no longer exists at all (Task 3 replaced the TVP insert with `SqlBulkCopy`). The only thing this script still needs to do is add `BCB_NEW2`'s surrogate `Id` key, which is specific to that one table, not something the generic engine does for every job.

Replace the entire contents of `sql/01_CreateSyncRunHistory_CBMS.sql` with:

```sql
-- Run on: CBMS database
-- One-time setup for dbo.BCB_NEW2: adds a surrogate BIGINT identity PK (Id), since
-- BCB_CMS_No is a plain copied-over source RowID, not a generated identity, and the
-- table has no other PK. Skipped if the table doesn't exist yet or already has it.
--
-- dbo.SyncRunHistory is NOT created by this script anymore — every job's target
-- database gets it auto-created in code (SqlSyncRunHistoryRepository.EnsureSchemaAsync)
-- the first time that job runs, since jobs can target any database, not just CBMS.
-- dbo.BcbRecordTableType no longer exists at all — the destination insert is
-- SqlBulkCopy now, not a table-valued parameter (see
-- docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md).

IF OBJECT_ID('dbo.BCB_NEW2', 'U') IS NOT NULL AND COL_LENGTH('dbo.BCB_NEW2', 'Id') IS NULL
BEGIN
    ALTER TABLE dbo.BCB_NEW2 ADD Id BIGINT IDENTITY(1,1) NOT NULL;
    ALTER TABLE dbo.BCB_NEW2 ADD CONSTRAINT PK_BCB_NEW2 PRIMARY KEY CLUSTERED (Id);
END
GO
```

- [ ] **Step 2: Update `docs/ARCHITECTURE.md`'s Purpose section and diagram**

Find the paragraph starting "CBMSB2BLink is a scheduled .NET 6 Windows console app..." and the diagram right after it. Replace both with:

```markdown
CBMSB2BLink is a scheduled .NET 6 Windows console app that runs a configured list of
independent sync jobs (`Sync:Jobs`), each copying new rows from its own source
database/query into its own target table. It keeps **no resume watermark of its own**
for any job — each job's source query is responsible for knowing what's already been
sent and excluding it. CBMSB2BLink only writes an audit trail of what each job's run
did (`SyncRunHistory`, in that job's own target database) — that table is history, not
something the app reads back to decide what to pull next. One job failing does not
stop the others in the same run (see "Multi-job orchestration" below).

```
                ┌───────────────────────┐
                │   CBMSB2BLink.exe      │
                │  (scheduled, one-shot) │
                └──────┬──────────┬──────┘
                       │          │
                 READ  │          │ WRITE (transactional, per job)
                       ▼          ▼
              each job's Source    each job's Target
              (proc/SQL + params)  (table, SyncRunHistory)
```

Today's one configured job (`BCB_NEW2`) reads `usp_GetBCBNewData` against CCRISB2B
(`src_tblRetRpt`/`src_tblCRARawReport`) and writes `dbo.BCB_NEW2` in CBMS — unchanged
from the pipeline described in
`docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md`, now expressed as one
entry in `Sync:Jobs` instead of hardcoded.

### Multi-job orchestration

`SyncEngine.RunAsync` acquires one process-level file lock covering the whole run,
then runs every configured job in `Sync:Jobs`, in order. Each job is isolated: if one
job's source is unreachable, its insert fails, or its source query's column count
doesn't match its configured `Target.Columns`, that job is recorded as `Failed` and
the run moves on to the next job rather than aborting. After all jobs finish, one
aggregate failure email lists every job that failed in that run (not one email per
job). The process exits non-zero if any job failed.
```
```

- [ ] **Step 3: Update `docs/ARCHITECTURE.md`'s "Why a TVP" section**

Find the `## Why a TVP instead of SqlBulkCopy` heading and its paragraph. Replace the whole section with:

```markdown
## Why `SqlBulkCopy` instead of a table-valued parameter

Earlier revisions of this app used a hand-written SQL Server table type
(`dbo.BcbRecordTableType`) matched exactly to one hardcoded destination table's
columns, so `INSERT ... OUTPUT INSERTED.CMS_NO SELECT ... FROM @Records` could do the
insert and report back a generated identity range in one round trip. That doesn't work
once the destination table (and its column list) is config, not code — there's no
per-job SQL Server type to write by hand. `SqlBulkCopy` needs no such type: it maps
the in-memory `DataTable`'s columns to `Target.Columns` positionally at runtime. The
key range it reports back is computed from the `DataTable` itself before the insert
even runs (the key is always the source's first column, copied straight through, never
a target-generated identity) — see
`docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md`, "Why no
OUTPUT-based identity capture".
```

- [ ] **Step 4: Update `StartPrompt.md`'s numbered flow**

Find the numbered list (`1. Read config` through `10. Schedule through:`). Replace steps 6 and 7 (`6. Bulk insert into: BCB_NEW2` / `7. Update SyncControl`) with:

```markdown
6. For each configured job (Sync:Jobs), in order:
   - Bulk insert its pulled rows into its own Target.Table (SqlBulkCopy)
   - Append a SyncRunHistory row in that job's own target database
   - One job failing doesn't stop the others
```

- [ ] **Step 5: Rebuild and run the full test suite**

Run: `dotnet build CBMSB2BLink.slnx && dotnet test CBMSB2BLink.slnx`
Expected: 0 build errors, all tests pass.

- [ ] **Step 6: Reset scratch databases and re-verify `BCB_NEW2` end-to-end through the new engine**

The `BCB_NEW2` job's source/target SQL is unchanged from the already-verified
`docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md` cutover — this proves
the generic engine reproduces that already-working behavior, not that the behavior
itself is new.

```powershell
sqlcmd -S ".\SQLEXPRESS" -E -C -d CBMS -i "sql\01_CreateSyncRunHistory_CBMS.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -i "sql\dev-seed_BCB_NEW2_CBMS_LocalTesting.sql"
sqlcmd -S ".\SQLEXPRESS" -E -C -Q "TRUNCATE TABLE CCRISB2B.dbo.CbmsB2BLink_SentLog; TRUNCATE TABLE CBMS.dbo.BCB_NEW2; DELETE FROM CBMS.dbo.SyncRunHistory WHERE SyncKey = 'BCB_NEW2';"
```

- [ ] **Step 7: Run the console app twice, same as the previous cutover's verification**

```powershell
dotnet build CBMSB2BLink.slnx
dotnet run --project src\CBMSB2BLink.Console
dotnet run --project src\CBMSB2BLink.Console
```

Expected: first run logs `Sync succeeded for BCB_NEW2: 100000 records, ...` (same count
as the earlier cutover's verification, since the underlying source/target SQL hasn't
changed); second run logs `No new records for BCB_NEW2.`

- [ ] **Step 8: Verify row counts**

```sql
SELECT COUNT(*) AS RowsInBcbNew2 FROM CBMS.dbo.BCB_NEW2;
SELECT COUNT(*) AS SentLogRows FROM CCRISB2B.dbo.CbmsB2BLink_SentLog;
SELECT TOP 5 * FROM CBMS.dbo.SyncRunHistory WHERE SyncKey = 'BCB_NEW2' ORDER BY RunId DESC;
```

Expected: `RowsInBcbNew2` = `SentLogRows` = 100000; `SyncRunHistory` shows the two runs
(`Success` then `NoNewData`), confirming `EnsureSchemaAsync` correctly auto-created the
table with no manual script needed for it.

- [ ] **Step 9: Commit**

```bash
git add sql/01_CreateSyncRunHistory_CBMS.sql docs/ARCHITECTURE.md StartPrompt.md
git commit -m "Trim BCB_NEW2 setup script, document multi-job orchestration, verify end-to-end"
```

