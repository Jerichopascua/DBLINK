using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace CBMSB2BLink.Core.Abstractions;

/// <summary>
/// A single open target-database connection + transaction shared by the destination
/// insert and the SyncRunHistory write for one job's run, so they commit or roll back
/// together — a crash mid-run leaves that job's target DB untouched for that run.
/// </summary>
public interface ITargetUnitOfWork : IAsyncDisposable
{
    DbTransaction Transaction { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
