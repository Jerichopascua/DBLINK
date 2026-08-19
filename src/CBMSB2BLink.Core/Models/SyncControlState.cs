using System;

namespace CBMSB2BLink.Core.Models;

/// <summary>
/// Current watermark for a sync key, read from CBMS dbo.SyncControl.
/// </summary>
public sealed class SyncControlState
{
    public string SyncKey { get; init; } = string.Empty;
    public long LastRowId { get; init; }
    public long? LastCmsNo { get; init; }
}
