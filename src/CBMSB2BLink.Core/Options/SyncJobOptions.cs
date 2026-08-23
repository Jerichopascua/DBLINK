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
