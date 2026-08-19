using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// A single open CBMS connection + transaction shared by the destination insert and the
/// SyncControl/SyncRunHistory writes for one sync run, so they commit or roll back
/// together (see SyncEngine step 5 — atomicity is what makes reruns after a crash safe
/// without needing a dedup column).
/// </summary>
public interface ICbmsUnitOfWork : IAsyncDisposable
{
    DbTransaction Transaction { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
