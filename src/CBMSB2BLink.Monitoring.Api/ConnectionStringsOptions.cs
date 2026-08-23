namespace CBMSB2BLink.Monitoring.Api;

/// <summary>
/// Local to this project (was CBMSB2BLink.Core.Options.ConnectionStringsOptions,
/// removed when the sync engine moved to per-job connection strings — see
/// docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md, decision 10).
/// The dashboard still reads one known SyncRunHistory database, unrelated to the
/// generic multi-job engine's per-job Source/Target connections.
/// </summary>
public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    public string Cbms { get; set; } = string.Empty;
}
