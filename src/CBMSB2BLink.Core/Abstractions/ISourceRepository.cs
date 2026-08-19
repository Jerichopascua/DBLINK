using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Reads new records from the source system. The SQL implementation calls
/// usp_GetBCBNewData directly against CCRISB2B. A future HttpSourceRepository can
/// implement this same contract against the source-side fallback bridge API
/// (see docs/ARCHITECTURE.md, "Phase 2") without any change to SyncEngine.
/// </summary>
public interface ISourceRepository
{
    Task<IReadOnlyList<BcbRecord>> GetNewRecordsAsync(long lastRowId, int batchSize, CancellationToken cancellationToken);
}
