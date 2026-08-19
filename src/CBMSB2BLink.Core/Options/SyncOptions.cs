using System.ComponentModel.DataAnnotations;

namespace CBMSB2BLink.Core.Options;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    [Required(AllowEmptyStrings = false)]
    public string SyncKey { get; set; } = "BCB_NEW";

    [Required(AllowEmptyStrings = false)]
    public string StoredProcedureName { get; set; } = "usp_GetBCBNewData";

    [Range(1, 100_000)]
    public int BatchSize { get; set; } = 5000;

    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 120;

    [Range(1, 86_400)]
    public int MaxRunDurationSeconds { get; set; } = 1800;

    /// <summary>
    /// "Sql" (default, direct DB pull) or "Http" (source-side fallback bridge — not yet implemented).
    /// </summary>
    public string SourceMode { get; set; } = "Sql";

    /// <summary>
    /// Path to the file lock used to prevent overlapping runs. Defaults to
    /// %ProgramData%\CBMSB2BLink\run.lock when left blank.
    /// </summary>
    public string? LockFilePath { get; set; }
}
