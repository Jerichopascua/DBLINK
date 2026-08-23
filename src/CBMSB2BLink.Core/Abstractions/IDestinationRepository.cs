using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Bulk-inserts a batch of rows into a job's target table, mapping DataTable columns
/// positionally to targetColumns (source and target column names are never assumed to
/// match).
/// </summary>
public interface IDestinationRepository
{
    Task<InsertBatchResult> InsertBatchAsync(ITargetUnitOfWork unitOfWork, string targetTable, IReadOnlyList<string> targetColumns, DataTable records, CancellationToken cancellationToken);
}
