using System;
using System.Threading;
using System.Threading.Tasks;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// Guards against an overlapping scheduled run racing another run's insert/history
/// writes. Defense in depth on top of Task Scheduler's "do not start a new instance"
/// setting.
/// </summary>
public interface IRunLock
{
    /// <summary>Returns a held lock, or null if another run already holds it.</summary>
    Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}
