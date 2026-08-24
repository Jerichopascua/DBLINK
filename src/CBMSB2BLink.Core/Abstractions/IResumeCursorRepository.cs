using System.Threading;
using System.Threading.Tasks;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Reads/writes each job's LastRowId watermark in its own target database
/// (dbo.CbmsB2BLink_ResumeCursor, one row per JobKey). Unlike SyncRunHistory (audit
/// only), this table IS read back to seed the next run's starting cursor — it is
/// advanced automatically on every successful run, and ops can also update it by hand
/// (e.g. `UPDATE dbo.CbmsB2BLink_ResumeCursor SET LastRowId = ... WHERE JobKey = ...`)
/// to force a specific resume point; the app always just reads whatever value is
/// currently there. See docs/ARCHITECTURE.md, "CBMS-side resume watermark" for the
/// accepted risk this reintroduces.
/// </summary>
public interface IResumeCursorRepository
{
    /// <summary>Creates dbo.CbmsB2BLink_ResumeCursor in the target database if it doesn't already exist.</summary>
    Task EnsureSchemaAsync(string targetConnectionString, CancellationToken cancellationToken);

    /// <summary>Returns the job's current LastRowId watermark, or 0 if no row exists yet for it.</summary>
    Task<long> GetLastRowIdAsync(string targetConnectionString, string jobKey, CancellationToken cancellationToken);

    /// <summary>Upserts the job's LastRowId watermark within the given unit of work's transaction.</summary>
    Task SetLastRowIdAsync(ITargetUnitOfWork unitOfWork, string jobKey, long lastRowId, CancellationToken cancellationToken);
}
