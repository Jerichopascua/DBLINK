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
/// columns positionally to targetColumns. No SQL Server table type needed. The
/// returned key range is computed from the DataTable itself (targetColumns[0]/column 0
/// is always the key, copied straight through from the source — never a target-side
/// generated identity), not from a database round trip — see
/// docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md, "Why no
/// OUTPUT-based identity capture".
/// </summary>
public sealed class SqlDestinationRepository : IDestinationRepository
{
    public async Task<InsertBatchResult> InsertBatchAsync(ITargetUnitOfWork unitOfWork, string targetTable, IReadOnlyList<string> targetColumns, DataTable records, CancellationToken cancellationToken)
    {
        var uow = (TargetUnitOfWork)unitOfWork;

        using var bulkCopy = new SqlBulkCopy(uow.Connection, SqlBulkCopyOptions.Default, (SqlTransaction)unitOfWork.Transaction)
        {
            DestinationTableName = targetTable
        };

        for (var i = 0; i < targetColumns.Count; i++)
        {
            bulkCopy.ColumnMappings.Add(i, targetColumns[i]);
        }

        await bulkCopy.WriteToServerAsync(records, cancellationToken);

        long? min = null;
        long? max = null;
        if (records.Rows.Count > 0)
        {
            min = System.Convert.ToInt64(records.Rows[0][0]);
            max = System.Convert.ToInt64(records.Rows[records.Rows.Count - 1][0]);
        }

        return new InsertBatchResult
        {
            RecordsInserted = records.Rows.Count,
            CmsNoFrom = min,
            CmsNoTo = max
        };
    }
}
