using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.Indexing;

public static class IndexingServices
{
    public static IServiceCollection AddMcpIndexing(this IServiceCollection services)
    {
        services.AddSingleton<IndexingActivityTracker>();
        services.AddHostedService<IndexingCoordinator>();
        return services;
    }
}
