using CBMSB2BLink.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CBMSB2BLink.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every CBMSB2BLink.Data piece — there is only one source mode now
    /// (direct SQL), so ISourceRepository is registered here too instead of being
    /// picked by the composition root.
    /// </summary>
    public static IServiceCollection AddCbmsB2BLinkData(this IServiceCollection services)
    {
        services.AddSingleton<ISourceRepository, SqlSourceRepository>();
        services.AddSingleton<IDestinationRepository, SqlDestinationRepository>();
        services.AddSingleton<ISyncRunHistoryRepository, SqlSyncRunHistoryRepository>();
        services.AddSingleton<IResumeCursorRepository, SqlResumeCursorRepository>();
        services.AddSingleton<ITargetUnitOfWorkFactory, TargetUnitOfWorkFactory>();
        return services;
    }
}
