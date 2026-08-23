using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Monitoring.Api;

/// <summary>
/// Read-only queries against CBMS dbo.SyncRunHistory for the dashboard. Deliberately
/// separate from CBMSB2BLink.Data's repositories — those are shaped around what
/// SyncEngine needs to write, not what a dashboard needs to read. There is no
/// SyncControl table to read a watermark from — "current position" is derived from the
/// most recent run that actually synced something (see GetStatusAsync), separately
/// from the most recent run overall (used for health/status).
/// </summary>
public sealed class SyncStatusReader
{
    private readonly string _connectionString;
    private readonly DashboardOptions _dashboardOptions;

    public SyncStatusReader(IOptions<ConnectionStringsOptions> connectionStrings, IOptions<DashboardOptions> dashboardOptions)
    {
        _connectionString = connectionStrings.Value.Cbms;
        _dashboardOptions = dashboardOptions.Value;
    }

    public async Task<SyncStatusDto?> GetStatusAsync(string syncKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Health/status come from the single most recent run, whatever its outcome.
        var latestRun = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            @"SELECT TOP 1 Status, CompletedUtc
              FROM dbo.SyncRunHistory
              WHERE SyncKey = @SyncKey
              ORDER BY RunId DESC;",
            new { SyncKey = syncKey },
            cancellationToken: cancellationToken));

        if (latestRun is null)
        {
            return null;
        }

        // "Last synced position" instead comes from the most recent run that actually
        // synced something — a NoNewData/Failed run has null SourceRowIdTo/CmsNoTo, and
        // showing null there (instead of the last real position) would make the
        // dashboard blank most of the time in normal operation, where most scheduled
        // runs find nothing new.
        var lastSyncedRun = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            @"SELECT TOP 1 SourceRowIdTo, CmsNoTo
              FROM dbo.SyncRunHistory
              WHERE SyncKey = @SyncKey AND SourceRowIdTo IS NOT NULL
              ORDER BY RunId DESC;",
            new { SyncKey = syncKey },
            cancellationToken: cancellationToken));

        string? lastRunStatus = latestRun.Status;
        DateTime? lastRunCompletedUtc = latestRun.CompletedUtc;
        long? lastRowId = lastSyncedRun?.SourceRowIdTo;
        long? lastCmsNo = lastSyncedRun?.CmsNoTo;
        var nowUtc = DateTime.UtcNow;

        return new SyncStatusDto(
            SyncKey: syncKey,
            LastRowId: lastRowId,
            LastCmsNo: lastCmsNo,
            LastRunStatus: lastRunStatus,
            LastRunCompletedUtc: lastRunCompletedUtc,
            MinutesSinceLastRun: HealthCalculator.MinutesSinceLastRun(lastRunCompletedUtc, nowUtc),
            IsHealthy: HealthCalculator.IsHealthy(lastRunCompletedUtc, lastRunStatus, _dashboardOptions.StalenessThresholdMinutes, nowUtc));
    }

    public async Task<IReadOnlyList<SyncRunDto>> GetRecentRunsAsync(string syncKey, int take, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<SyncRunDto>(new CommandDefinition(
            @"SELECT TOP (@Take)
                  RunId, SyncKey, StartedUtc, CompletedUtc, Status,
                  SourceRowIdFrom, SourceRowIdTo, CmsNoFrom, CmsNoTo,
                  RecordsRead, RecordsInserted, ErrorMessage, HostMachine, DurationMs
              FROM dbo.SyncRunHistory
              WHERE SyncKey = @SyncKey
              ORDER BY RunId DESC;",
            new { SyncKey = syncKey, Take = take },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public IReadOnlyList<string> ConfiguredSyncKeys => _dashboardOptions.SyncKeys;
}
