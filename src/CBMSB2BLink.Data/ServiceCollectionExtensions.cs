using CBMSB2BLink.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CBMSB2BLink.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCbmsB2BLinkData(this IServiceCollection services)
    {
        services.AddSingleton<ISourceRepository, SqlSourceRepository>();
        services.AddSingleton<IDestinationRepository, SqlDestinationRepository>();
        services.AddSingleton<ISyncControlRepository, SqlSyncControlRepository>();
        services.AddSingleton<ICbmsUnitOfWorkFactory, CbmsUnitOfWorkFactory>();
        return services;
    }
}
