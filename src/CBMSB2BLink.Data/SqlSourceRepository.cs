using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Data;

/// <summary>
/// Direct-SQL implementation of ISourceRepository: EXEC usp_GetBCBNewData against
/// CCRISB2B. A future HttpSourceRepository (the source-side fallback bridge, see
/// docs/ARCHITECTURE.md) implements the same interface for when this connection is
/// unreachable.
/// </summary>
public sealed class SqlSourceRepository : ISourceRepository
{
    private sealed class BcbRecordRow
    {
        public long ROWID { get; init; }
        public int BCB_CMS_No { get; init; }
        public string? BCB_IdNo1 { get; init; }
        public string? BCB_IdNo2 { get; init; }
        public string? BCB_Name1 { get; init; }
        public string? BCB_DOB { get; init; }
        public string? BCB_Nationality { get; init; }
        public System.DateTime? BCB_CreateDate { get; init; }
        public string? BCB_LastUpdateBy { get; init; }
        public string? BCB_ENTKEY { get; init; }
        public string? BCB_RefNo { get; init; }
        public string? BCB_SCR_Scored_TxnCode { get; init; }
    }

    private readonly string _connectionString;
    private readonly SyncOptions _syncOptions;

    public SqlSourceRepository(IOptions<ConnectionStringsOptions> connectionStrings, IOptions<SyncOptions> syncOptions)
    {
        _connectionString = connectionStrings.Value.CcrisB2B;
        _syncOptions = syncOptions.Value;
    }

    public async Task<IReadOnlyList<BcbRecord>> GetNewRecordsAsync(long lastRowId, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            _syncOptions.StoredProcedureName,
            new { LastRowId = lastRowId, BatchSize = batchSize },
            commandType: CommandType.StoredProcedure,
            commandTimeout: _syncOptions.CommandTimeoutSeconds,
            cancellationToken: cancellationToken);

        var rows = await connection.QueryAsync<BcbRecordRow>(command);

        return rows
            .Select(r => new BcbRecord
            {
                RowId = r.ROWID,
                BcbCmsNo = r.BCB_CMS_No,
                BcbIdNo1 = r.BCB_IdNo1,
                BcbIdNo2 = r.BCB_IdNo2,
                BcbName1 = r.BCB_Name1,
                BcbDob = r.BCB_DOB,
                BcbNationality = r.BCB_Nationality,
                BcbCreateDate = r.BCB_CreateDate,
                BcbLastUpdateBy = r.BCB_LastUpdateBy,
                BcbEntKey = r.BCB_ENTKEY,
                BcbRefNo = r.BCB_RefNo,
                BcbScrScoredTxnCode = r.BCB_SCR_Scored_TxnCode
            })
            .OrderBy(r => r.RowId)
            .ToList();
    }
}
