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

    /// <summary>Rows requested per source call — the paging chunk size.</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>
    /// Hard cap on total rows one run will accumulate for this job across all pages.
    /// SyncEngine.PullAllPagesAsync stops paging once this many rows have been
    /// pulled, even if the last page was full and more data remains — the rest waits
    /// for the next run (resumed via dbo.CbmsB2BLink_ResumeCursor). A safety valve
    /// independent of Sync:MaxRunDurationSeconds, so one run can't accidentally pull
    /// an unbounded backlog into memory.
    /// </summary>
    public int BatchAllowedMaxRecord { get; set; } = 100_000;

    public int CommandTimeoutSeconds { get; set; } = 120;
}
