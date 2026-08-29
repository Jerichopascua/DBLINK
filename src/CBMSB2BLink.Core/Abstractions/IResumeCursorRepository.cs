using System.Threading;
using System.Threading.Tasks;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Computes each job's starting @LastRowId cursor from dbo.SyncRunHistory — an
/// app-owned table, never a BAU source/target table — rather than from the target
/// table's own data. Each page's SyncRunHistory row is written in the SAME
/// transaction as that page's actual insert (see SyncEngine), so this can never drift
/// out of sync with what was really committed, and — unlike reading the target
/// table's own key column — it doesn't assume anything about that column's identity:
/// a BAU target table's "key" column can be a server-generated IDENTITY unrelated to
/// the source's RowID (discovered on dbo.BCB_RSP_CRDCR, where the SP-return's key
/// silently gets discarded and replaced by SQL Server's own auto-increment during
/// SqlBulkCopy). SyncRunHistory.SourceRowIdTo records the source's own RowID
/// regardless of what the target's columns do with it, so it's a safe universal
/// source of truth for every job, whatever its target schema looks like — and it
/// requires no changes to any existing BAU table.
/// </summary>
public interface IResumeCursorRepository
{
    /// <summary>
    /// Returns MAX(SourceRowIdTo) from dbo.SyncRunHistory for this job's successful
    /// runs, or 0 if there are none yet.
    /// </summary>
    Task<long> GetLastRowIdAsync(string targetConnectionString, string jobKey, CancellationToken cancellationToken);
}
