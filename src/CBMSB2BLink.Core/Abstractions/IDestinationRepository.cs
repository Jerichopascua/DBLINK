using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Inserts a batch of records into CBMS dbo.BCB_NEW within the given unit of work and
/// returns the generated CMS_NO range (needed for the SyncControl watermark).
/// </summary>
public interface IDestinationRepository
{
    Task<InsertBatchResult> InsertBatchAsync(ICbmsUnitOfWork unitOfWork, IReadOnlyList<BcbRecord> records, CancellationToken cancellationToken);
}
