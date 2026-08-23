using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Audit-only: appends SyncRunHistory rows in each job's own target database.
/// CBMSB2BLink does not track a resume watermark itself for any job — the source-side
/// query is responsible for knowing what's already been sent.
/// </summary>
public interface ISyncRunHistoryRepository
{
    /// <summary>Creates dbo.SyncRunHistory in the target database if it doesn't already exist.</summary>
    Task EnsureSchemaAsync(string targetConnectionString, CancellationToken cancellationToken);

    /// <summary>Appends a SyncRunHistory row within the given unit of work (Success / NoNewData path).</summary>
    Task RecordRunAsync(ITargetUnitOfWork unitOfWork, SyncRunResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort append of a Failed SyncRunHistory row on its own connection, used after the
    /// write transaction has already been rolled back (or never opened).
    /// </summary>
    Task RecordFailedRunAsync(string targetConnectionString, SyncRunResult result, CancellationToken cancellationToken);
}
