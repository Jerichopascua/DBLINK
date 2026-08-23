namespace CBMSB2BLink.Core.Models;

/// <summary>
/// Result of bulk-inserting a batch of records into a job's target table. The key
/// range isn't reported here — it's always identical to SyncRunResult.SourceRowIdFrom/To
/// (the key is copied straight through, never target-generated), so SyncEngine assigns
/// SyncRunResult.CmsNoFrom/To directly instead of this type recomputing the same range.
/// </summary>
public sealed class InsertBatchResult
{
    public int RecordsInserted { get; init; }
}
