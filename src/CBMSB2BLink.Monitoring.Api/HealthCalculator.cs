using System;

namespace CBMSB2BLink.Monitoring.Api;

/// <summary>
/// Pure staleness/health logic, pulled out of the endpoint handler so it's directly
/// unit-testable without a database or web host. Inputs are the most recent
/// SyncRunHistory row for a sync key — that table is the only source of truth for
/// status now (there is no SyncControl table), so a run of consecutive failures is
/// always visible here.
/// </summary>
public static class HealthCalculator
{
    public static double? MinutesSinceLastRun(DateTime? lastRunCompletedUtc, DateTime nowUtc)
    {
        if (lastRunCompletedUtc is null)
        {
            return null;
        }

        return (nowUtc - lastRunCompletedUtc.Value).TotalMinutes;
    }

    /// <summary>
    /// Healthy means: at least one run has ever completed, its status wasn't Failed
    /// (Success and NoNewData both count — NoNewData just means nothing to sync, not
    /// broken), and it happened within the staleness threshold.
    /// </summary>
    public static bool IsHealthy(DateTime? lastRunCompletedUtc, string? lastRunStatus, int staleThresholdMinutes, DateTime nowUtc)
    {
        if (lastRunCompletedUtc is null || string.IsNullOrEmpty(lastRunStatus))
        {
            return false;
        }

        if (string.Equals(lastRunStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var minutesSince = MinutesSinceLastRun(lastRunCompletedUtc, nowUtc);
        return minutesSince is not null && minutesSince.Value <= staleThresholdMinutes;
    }
}
