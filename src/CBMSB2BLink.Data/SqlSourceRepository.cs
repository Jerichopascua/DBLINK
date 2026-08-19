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
        public string IDNO { get; init; } = string.Empty;
        public System.DateTime CREATEDATE { get; init; }
        public decimal AMOUNT { get; init; }
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
                IdNo = r.IDNO,
                CreateDate = r.CREATEDATE,
                Amount = r.AMOUNT
            })
            .OrderBy(r => r.RowId)
            .ToList();
    }
}
