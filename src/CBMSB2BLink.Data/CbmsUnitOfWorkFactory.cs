using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Data;

public sealed class CbmsUnitOfWorkFactory : ICbmsUnitOfWorkFactory
{
    private readonly string _connectionString;

    public CbmsUnitOfWorkFactory(IOptions<ConnectionStringsOptions> connectionStrings)
    {
        _connectionString = connectionStrings.Value.Cbms;
    }

    public ICbmsUnitOfWork Create() => new CbmsUnitOfWork(_connectionString);
}
