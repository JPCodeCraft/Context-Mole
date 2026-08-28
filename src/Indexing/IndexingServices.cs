using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Indexing;

public static class IndexingServices
{
    public static IServiceCollection AddContextMoleIndexing(this IServiceCollection services)
    {
        services.AddSingleton<IndexingActivityTracker>();
        services.AddSingleton<EmbeddingPolicyRefreshTracker>();
        services.AddSingleton<IndexingCoordinator>();
        services.AddSingleton<IProjectIndexingControl>(provider =>
            provider.GetRequiredService<IndexingCoordinator>());
        services.AddHostedService(provider => provider.GetRequiredService<IndexingCoordinator>());
        return services;
    }
}
