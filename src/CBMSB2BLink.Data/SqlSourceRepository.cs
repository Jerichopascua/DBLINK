using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Options;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

/// <summary>
/// Direct-SQL implementation of ISourceRepository: executes the job's configured
/// Source.CommandText (proc or raw SQL) with @LastRowId/@BatchSize and returns the raw
/// result set as a DataTable — column shape comes entirely from what the query
/// returns, not a hardcoded model.
/// </summary>
public sealed class SqlSourceRepository : ISourceRepository
{
    public async Task<DataTable> GetNewRecordsAsync(SourceJobOptions source, long lastRowId, int batchSize, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(source.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = source.CommandText;
        command.CommandType = source.CommandType == "Text" ? CommandType.Text : CommandType.StoredProcedure;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@LastRowId", lastRowId);
        command.Parameters.AddWithValue("@BatchSize", batchSize);

        var table = new DataTable();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        table.Load(reader);
        return table;
    }
}
