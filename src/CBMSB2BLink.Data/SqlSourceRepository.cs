using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace CBMSB2BLink.Data;

/// <summary>
/// Direct-SQL implementation of ISourceRepository: executes the job's configured
/// Source.CommandText (proc or raw SQL) with @LastRowId/@BatchSize and returns the raw
/// result set as a DataTable — column shape comes entirely from what the query
/// returns, not a hardcoded model.
/// </summary>
/// //lito here
public sealed class SqlSourceRepository : ISourceRepository
{
    private readonly ILogger<SqlSourceRepository> _logger;

    public SqlSourceRepository(ILogger<SqlSourceRepository> logger)
    {
        _logger = logger;
    }

    public async Task<DataTable> GetNewRecordsAsync(SourceJobOptions source, long lastRowId, int batchSize, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "GetNewRecordsAsync starting: CommandText={CommandText}, LastRowId={LastRowId}, BatchSize={BatchSize}, CommandTimeoutSeconds={CommandTimeoutSeconds}",
            source.CommandText, lastRowId, batchSize, commandTimeoutSeconds);

        try
        {
            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            _logger.LogInformation("GetNewRecordsAsync connection opened after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            await using var command = connection.CreateCommand();
            command.CommandText = source.CommandText;
            command.CommandType = source.CommandType == "Text" ? CommandType.Text : CommandType.StoredProcedure;
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.AddWithValue("@LastRowId", lastRowId);
            command.Parameters.AddWithValue("@BatchSize", batchSize);

            var table = new DataTable();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            _logger.LogInformation("GetNewRecordsAsync reader returned after {ElapsedMs}ms, loading rows", stopwatch.ElapsedMilliseconds);

            table.Load(reader);
            _logger.LogInformation(
                "GetNewRecordsAsync finished: {RowCount} row(s), {ColumnCount} column(s), {ElapsedMs}ms total",
                table.Rows.Count, table.Columns.Count, stopwatch.ElapsedMilliseconds);

            return table;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetNewRecordsAsync failed after {ElapsedMs}ms (LastRowId={LastRowId}, BatchSize={BatchSize})", stopwatch.ElapsedMilliseconds, lastRowId, batchSize);
            throw;
        }
    }
}
