using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

public sealed class SqlResumeCursorRepository : IResumeCursorRepository
{
    public async Task EnsureSchemaAsync(string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(EnsureSchemaSql, cancellationToken: cancellationToken));
    }

    public async Task<long> GetLastRowIdAsync(string targetConnectionString, string jobKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(SelectLastRowIdSql, new { JobKey = jobKey }, cancellationToken: cancellationToken);
        var lastRowId = await connection.ExecuteScalarAsync<long?>(command);
        return lastRowId ?? 0L;
    }

    public async Task SetLastRowIdAsync(ITargetUnitOfWork unitOfWork, string jobKey, long lastRowId, CancellationToken cancellationToken)
    {
        var uow = (TargetUnitOfWork)unitOfWork;

        var command = new CommandDefinition(UpsertLastRowIdSql, new { JobKey = jobKey, LastRowId = lastRowId }, transaction: uow.Transaction, cancellationToken: cancellationToken);
        await uow.Connection.ExecuteAsync(command);
    }

    private const string EnsureSchemaSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CbmsB2BLink_ResumeCursor' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CbmsB2BLink_ResumeCursor
    (
        JobKey      VARCHAR(50) NOT NULL CONSTRAINT PK_CbmsB2BLink_ResumeCursor PRIMARY KEY,
        LastRowId   BIGINT      NOT NULL,
        DateUpdated DATETIME2   NOT NULL
    );
END";

    private const string SelectLastRowIdSql = @"
SELECT LastRowId FROM dbo.CbmsB2BLink_ResumeCursor WHERE JobKey = @JobKey;";

    private const string UpsertLastRowIdSql = @"
MERGE INTO dbo.CbmsB2BLink_ResumeCursor AS tgt
USING (SELECT @JobKey AS JobKey, @LastRowId AS LastRowId) AS src
    ON tgt.JobKey = src.JobKey
WHEN MATCHED THEN
    UPDATE SET LastRowId = src.LastRowId, DateUpdated = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (JobKey, LastRowId, DateUpdated) VALUES (src.JobKey, src.LastRowId, SYSUTCDATETIME());";
}
