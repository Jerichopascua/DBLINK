using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

/// <summary>
/// Bulk-inserts a batch into the job's target table via SqlBulkCopy, mapping DataTable
/// columns positionally to targetColumns. No SQL Server table type needed. The key
/// range isn't reported here — see InsertBatchResult.
/// </summary>
public sealed class SqlDestinationRepository : IDestinationRepository
{
    public async Task<InsertBatchResult> InsertBatchAsync(ITargetUnitOfWork unitOfWork, string targetTable, IReadOnlyList<string> targetColumns, DataTable records, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var uow = (TargetUnitOfWork)unitOfWork;

        using var bulkCopy = new SqlBulkCopy(uow.Connection, SqlBulkCopyOptions.Default, (SqlTransaction)unitOfWork.Transaction)
        {
            DestinationTableName = targetTable,
            BulkCopyTimeout = commandTimeoutSeconds
        };

        for (var i = 0; i < targetColumns.Count; i++)
        {
            bulkCopy.ColumnMappings.Add(i, targetColumns[i]);
        }

        await bulkCopy.WriteToServerAsync(records, cancellationToken);

        return new InsertBatchResult
        {
            RecordsInserted = records.Rows.Count
        };
    }
}
