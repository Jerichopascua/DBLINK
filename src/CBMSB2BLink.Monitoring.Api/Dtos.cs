using System;

namespace CBMSB2BLink.Monitoring.Api;

public sealed record SyncStatusDto(
    string SyncKey,
    long? LastRowId,
    long? LastCmsNo,
    string? LastRunStatus,
    DateTime? LastRunCompletedUtc,
    double? MinutesSinceLastRun,
    bool IsHealthy);

public sealed record SyncRunDto(
    long RunId,
    string SyncKey,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Status,
    long? SourceRowIdFrom,
    long? SourceRowIdTo,
    long? CmsNoFrom,
    long? CmsNoTo,
    int RecordsRead,
    int RecordsInserted,
    string? ErrorMessage,
    string? HostMachine,
    double? DurationSeconds);
