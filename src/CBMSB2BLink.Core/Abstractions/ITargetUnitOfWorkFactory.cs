namespace CBMSB2BLink.Core.Abstractions;

public interface ITargetUnitOfWorkFactory
{
    /// <summary>Opens a unit of work against the given job's target connection string.</summary>
    ITargetUnitOfWork Create(string connectionString);
}
