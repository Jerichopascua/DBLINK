using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

public sealed class SqlSyncRunHistoryRepository : ISyncRunHistoryRepository
{
    public async Task EnsureSchemaAsync(string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(EnsureSchemaSql, cancellationToken: cancellationToken));
    }

    public async Task RecordRunAsync(ITargetUnitOfWork unitOfWork, SyncRunResult result, CancellationToken cancellationToken)
    {
        var uow = (TargetUnitOfWork)unitOfWork;

        var command = new CommandDefinition(InsertRunHistorySql, ToParams(result), transaction: uow.Transaction, cancellationToken: cancellationToken);
        await uow.Connection.ExecuteAsync(command);
    }

    public async Task RecordFailedRunAsync(string targetConnectionString, SyncRunResult result, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(InsertRunHistorySql, ToParams(result), cancellationToken: cancellationToken));
    }

    private const string EnsureSchemaSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncRunHistory' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SyncRunHistory
    (
        RunId           BIGINT IDENTITY(1,1) PRIMARY KEY,
        SyncKey         VARCHAR(50)   NOT NULL,
        StartedUtc      DATETIME2     NOT NULL,
        CompletedUtc    DATETIME2     NULL,
        Status          VARCHAR(20)   NOT NULL,
        SourceRowIdFrom BIGINT        NULL,
        SourceRowIdTo   BIGINT        NULL,
        CmsNoFrom       BIGINT        NULL,
        CmsNoTo         BIGINT        NULL,
        RecordsRead     INT           NOT NULL CONSTRAINT DF_SyncRunHistory_RecordsRead DEFAULT (0),
        RecordsInserted INT           NOT NULL CONSTRAINT DF_SyncRunHistory_RecordsInserted DEFAULT (0),
        ErrorMessage    NVARCHAR(MAX) NULL,
        HostMachine     VARCHAR(100)  NULL,
        DurationSeconds FLOAT         NULL
    );

    CREATE INDEX IX_SyncRunHistory_SyncKey_StartedUtc ON dbo.SyncRunHistory (SyncKey, StartedUtc DESC);
END";

    private const string InsertRunHistorySql = @"
INSERT INTO dbo.SyncRunHistory
    (SyncKey, StartedUtc, CompletedUtc, Status, SourceRowIdFrom, SourceRowIdTo,
     CmsNoFrom, CmsNoTo, RecordsRead, RecordsInserted, ErrorMessage, HostMachine, DurationSeconds)
VALUES
    (@SyncKey, @StartedUtc, @CompletedUtc, @Status, @SourceRowIdFrom, @SourceRowIdTo,
     @CmsNoFrom, @CmsNoTo, @RecordsRead, @RecordsInserted, @ErrorMessage, @HostMachine, @DurationSeconds);";

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
        result.DurationSeconds
    };
}
