using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Options;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Reads one page of new records from a job's source. Column shape comes entirely
/// from what the job's configured query returns — see
/// docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md.
/// </summary>
public interface ISourceRepository
{
    Task<DataTable> GetNewRecordsAsync(SourceJobOptions source, long lastRowId, int batchSize, int commandTimeoutSeconds, CancellationToken cancellationToken);
}
