using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Data;

public sealed class SqlSyncControlRepository : ISyncControlRepository
{
    private readonly string _connectionString;

    public SqlSyncControlRepository(IOptions<ConnectionStringsOptions> connectionStrings)
    {
        _connectionString = connectionStrings.Value.Cbms;
    }

    public async Task<SyncControlState> GetWatermarkAsync(string syncKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            "SELECT SyncKey, LastRowId, LastCmsNo FROM dbo.SyncControl WHERE SyncKey = @SyncKey;",
            new { SyncKey = syncKey },
            cancellationToken: cancellationToken);

        var state = await connection.QuerySingleOrDefaultAsync<SyncControlState>(command);
        if (state is not null)
        {
            return state;
        }

        // First run for this key: seed the row so subsequent updates are plain UPDATEs.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO dbo.SyncControl (SyncKey, LastRowId) VALUES (@SyncKey, 0);",
            new { SyncKey = syncKey },
            cancellationToken: cancellationToken));

        return new SyncControlState { SyncKey = syncKey, LastRowId = 0, LastCmsNo = null };
    }

    public async Task UpdateWatermarkAsync(ICbmsUnitOfWork unitOfWork, string syncKey, long lastRowId, long? lastCmsNo, CancellationToken cancellationToken)
    {
        var uow = (CbmsUnitOfWork)unitOfWork;

        var command = new CommandDefinition(
            @"UPDATE dbo.SyncControl
              SET LastRowId = @LastRowId,
                  LastCmsNo = @LastCmsNo,
                  LastSyncStartUtc = @Now,
                  LastSyncEndUtc = @Now,
                  LastSyncStatus = 'Success',
                  UpdatedAtUtc = @Now
              WHERE SyncKey = @SyncKey;",
            new { SyncKey = syncKey, LastRowId = lastRowId, LastCmsNo = lastCmsNo, Now = System.DateTime.UtcNow },
            transaction: uow.Transaction,
            cancellationToken: cancellationToken);

        await uow.Connection.ExecuteAsync(command);
    }

    public async Task RecordRunAsync(ICbmsUnitOfWork unitOfWork, SyncRunResult result, CancellationToken cancellationToken)
    {
        var uow = (CbmsUnitOfWork)unitOfWork;

        var command = new CommandDefinition(InsertRunHistorySql, ToParams(result), transaction: uow.Transaction, cancellationToken: cancellationToken);
        await uow.Connection.ExecuteAsync(command);
    }

    public async Task RecordFailedRunAsync(SyncRunResult result, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(InsertRunHistorySql, ToParams(result), cancellationToken: cancellationToken));
    }

    private const string InsertRunHistorySql = @"
INSERT INTO dbo.SyncRunHistory
    (SyncKey, StartedUtc, CompletedUtc, Status, SourceRowIdFrom, SourceRowIdTo,
     CmsNoFrom, CmsNoTo, RecordsRead, RecordsInserted, ErrorMessage, HostMachine, DurationMs)
VALUES
    (@SyncKey, @StartedUtc, @CompletedUtc, @Status, @SourceRowIdFrom, @SourceRowIdTo,
     @CmsNoFrom, @CmsNoTo, @RecordsRead, @RecordsInserted, @ErrorMessage, @HostMachine, @DurationMs);";

    private static object ToParams(SyncRunResult result) => new
    {
        result.SyncKey,
        result.StartedUtc,
        result.CompletedUtc,
        Status = result.Status.ToString(),
        result.SourceRowIdFrom,
        result.SourceRowIdTo,
        result.CmsNoFrom,
        result.CmsNoTo,
        result.RecordsRead,
        result.RecordsInserted,
        result.ErrorMessage,
        result.HostMachine,
        result.DurationMs
    };
}
