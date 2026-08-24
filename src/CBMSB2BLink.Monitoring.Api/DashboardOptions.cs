namespace CBMSB2BLink.Monitoring.Api;

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>
    /// Deliberately defaults to empty, not ["BCB_NEW"] — Microsoft.Extensions.Configuration
    /// appends config array values onto a pre-existing non-empty default array instead of
    /// replacing it, so a non-empty default here plus a configured "Dashboard:SyncKeys"
    /// section would silently duplicate entries. The "BCB_NEW" fallback is applied via
    /// PostConfigure in Program.cs after binding instead.
    /// </summary>
    public string[] SyncKeys { get; set; } = System.Array.Empty<string>();

    public int StalenessThresholdMinutes { get; set; } = 60;
}
