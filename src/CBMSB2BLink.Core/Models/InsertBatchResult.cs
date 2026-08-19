namespace CBMSB2BLink.Core.Models;

/// <summary>
/// Result of bulk-inserting a batch of records into CBMS dbo.BCB_NEW.
/// </summary>
public sealed class InsertBatchResult
{
    public int RecordsInserted { get; init; }
    public long? CmsNoFrom { get; init; }
    public long? CmsNoTo { get; init; }
}
