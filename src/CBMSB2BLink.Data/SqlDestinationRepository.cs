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
/// Inserts a batch into CBMS dbo.BCB_NEW2 via a table-valued parameter
/// (dbo.BcbRecordTableType, see sql/01_CreateSyncRunHistory_CBMS.sql). No dedup filter
/// here — the CCRISB2B-side usp_GetBCBNewData is responsible for never returning an
/// already-sent row (see docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md).
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
        table.Columns.Add("BCB_CMS_No", typeof(int));
        table.Columns.Add("BCB_IdNo1", typeof(string));
        table.Columns.Add("BCB_IdNo2", typeof(string));
        table.Columns.Add("BCB_Name1", typeof(string));
        table.Columns.Add("BCB_DOB", typeof(string));
        table.Columns.Add("BCB_Nationality", typeof(string));
        table.Columns.Add("BCB_CreateDate", typeof(System.DateTime));
        table.Columns.Add("BCB_LastUpdateBy", typeof(string));
        table.Columns.Add("BCB_ENTKEY", typeof(string));
        table.Columns.Add("BCB_RefNo", typeof(string));
        table.Columns.Add("BCB_SCR_Scored_TxnCode", typeof(string));

        foreach (var record in records)
        {
            table.Rows.Add(
                record.BcbCmsNo,
                record.BcbIdNo1,
                record.BcbIdNo2,
                record.BcbName1,
                record.BcbDob,
                record.BcbNationality,
                record.BcbCreateDate,
                record.BcbLastUpdateBy,
                record.BcbEntKey,
                record.BcbRefNo,
                record.BcbScrScoredTxnCode);
        }

        var command = uow.Connection.CreateCommand();
        command.Transaction = (SqlTransaction)unitOfWork.Transaction;
        command.CommandTimeout = _commandTimeoutSeconds;
        command.CommandText = @"
INSERT INTO dbo.BCB_NEW2
    (BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
     BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode)
OUTPUT INSERTED.BCB_CMS_No
SELECT BCB_CMS_No, BCB_IdNo1, BCB_IdNo2, BCB_Name1, BCB_DOB, BCB_Nationality,
       BCB_CreateDate, BCB_LastUpdateBy, BCB_ENTKEY, BCB_RefNo, BCB_SCR_Scored_TxnCode
FROM @Records;";

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
                var cmsNo = reader.GetInt32(0);
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
