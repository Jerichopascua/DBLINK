using System;

namespace CBMSB2BLink.Core.Models;

public enum SyncRunStatus
{
    Success,
    NoNewData,
    Failed
}

/// <summary>
/// Outcome of a single SyncEngine.RunAsync execution, persisted to dbo.SyncRunHistory.
/// </summary>
public sealed class SyncRunResult
{
    public string SyncKey { get; init; } = string.Empty;
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; set; }
    public SyncRunStatus Status { get; set; } = SyncRunStatus.Failed;
    public long? SourceRowIdFrom { get; set; }
    public long? SourceRowIdTo { get; set; }
    public long? CmsNoFrom { get; set; }
    public long? CmsNoTo { get; set; }
    public int RecordsRead { get; set; }
    public int RecordsInserted { get; set; }
    public string? ErrorMessage { get; set; }
    public string HostMachine { get; init; } = Environment.MachineName;
    public int? DurationMs { get; set; }
}
