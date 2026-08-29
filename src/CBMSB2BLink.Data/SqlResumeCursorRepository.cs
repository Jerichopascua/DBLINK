using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

public sealed class SqlResumeCursorRepository : IResumeCursorRepository
{
    public async Task<long> GetLastRowIdAsync(string targetConnectionString, string jobKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(SelectLastRowIdSql, new { JobKey = jobKey }, cancellationToken: cancellationToken);
        var lastRowId = await connection.ExecuteScalarAsync<long?>(command);
        return lastRowId ?? 0L;
    }

    private const string SelectLastRowIdSql = @"
SELECT MAX(SourceRowIdTo) FROM dbo.SyncRunHistory WHERE SyncKey = @JobKey AND Status = 'Success';";
}
