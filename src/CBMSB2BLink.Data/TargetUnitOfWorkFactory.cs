using CBMSB2BLink.Core.Abstractions;

namespace CBMSB2BLink.Data;

public sealed class TargetUnitOfWorkFactory : ITargetUnitOfWorkFactory
{
    public ITargetUnitOfWork Create(string connectionString) => new TargetUnitOfWork(connectionString);
}
