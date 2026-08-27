using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Indexing;

public static class IndexingServices
{
    public static IServiceCollection AddContextMoleIndexing(this IServiceCollection services)
    {
        services.AddSingleton<IndexingActivityTracker>();
        services.AddSingleton<EmbeddingPolicyRefreshTracker>();
        services.AddHostedService<IndexingCoordinator>();
        return services;
    }
}