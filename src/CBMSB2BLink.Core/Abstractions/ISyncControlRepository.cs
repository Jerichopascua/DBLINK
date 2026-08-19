using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

public interface ISyncControlRepository
{
    /// <summary>Reads the current watermark on its own short-lived connection (outside the write transaction).</summary>
    Task<SyncControlState> GetWatermarkAsync(string syncKey, CancellationToken cancellationToken);

    /// <summary>Advances the watermark within the given unit of work. Called only on a successful batch insert.</summary>
    Task UpdateWatermarkAsync(ICbmsUnitOfWork unitOfWork, string syncKey, long lastRowId, long? lastCmsNo, CancellationToken cancellationToken);

    /// <summary>Appends a SyncRunHistory row within the given unit of work (Success / NoNewData path).</summary>
    Task RecordRunAsync(ICbmsUnitOfWork unitOfWork, SyncRunResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort append of a Failed SyncRunHistory row on its own connection, used after the
    /// write transaction has already been rolled back (or never opened, e.g. source unreachable).
    /// </summary>
    Task RecordFailedRunAsync(SyncRunResult result, CancellationToken cancellationToken);
}
