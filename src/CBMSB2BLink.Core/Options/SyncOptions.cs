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
