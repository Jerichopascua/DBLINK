using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Data;

/// <summary>
/// Inserts a batch into CBMS dbo.BCB_NEW via a table-valued parameter (dbo.BcbRecordTableType,
/// see sql/01_CreateSyncControlAndRunHistory_CBMS.sql) and captures the generated CMS_NO
/// range from OUTPUT INSERTED.CMS_NO. A TVP is used instead of SqlBulkCopy because
/// SqlBulkCopy does not reliably surface generated identity values, and we need the
/// CmsNo range for the SyncControl watermark.
/// </summary>
public sealed class SqlDestinationRepository : IDestinationRepository
{
    private readonly int _commandTimeoutSeconds;

    public SqlDestinationRepository(IOptions<SyncOptions> syncOptions)
    {
        _commandTimeoutSeconds = syncOptions.Value.CommandTimeoutSeconds;
    }

    public async Task<InsertBatchResult> InsertBatchAsync(ICbmsUnitOfWork unitOfWork, IReadOnlyList<BcbRecord> records, CancellationToken cancellationToken)
    {
        var uow = (CbmsUnitOfWork)unitOfWork;

        var table = new DataTable();
        table.Columns.Add("IdNo", typeof(string));
        table.Columns.Add("CreateDate", typeof(System.DateTime));
        table.Columns.Add("Amount", typeof(decimal));
        foreach (var record in records)
        {
            table.Rows.Add(record.IdNo, record.CreateDate, record.Amount);
        }

        var command = uow.Connection.CreateCommand();
        command.Transaction = (SqlTransaction)unitOfWork.Transaction;
        command.CommandTimeout = _commandTimeoutSeconds;
        command.CommandText = @"
INSERT INTO dbo.BCB_NEW (IDNO, CREATEDATE, AMOUNT)
OUTPUT INSERTED.CMS_NO
SELECT IdNo, CreateDate, Amount FROM @Records;";

        var tvp = command.Parameters.AddWithValue("@Records", table);
        tvp.SqlDbType = SqlDbType.Structured;
        tvp.TypeName = "dbo.BcbRecordTableType";

        long? min = null;
        long? max = null;
        var count = 0;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var cmsNo = reader.GetInt64(0);
                count++;
                if (min is null || cmsNo < min) min = cmsNo;
                if (max is null || cmsNo > max) max = cmsNo;
            }
        }

        return new InsertBatchResult
        {
            RecordsInserted = count,
            CmsNoFrom = min,
            CmsNoTo = max
        };
    }
}
