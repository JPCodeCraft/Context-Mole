using MCPIndexSearch.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.Search;

public static class SearchServices
{
    public static IServiceCollection AddMcpIndexSearch(this IServiceCollection services) => services
        .AddSingleton<IVectorIndexFactory, FlatVectorIndexFactory>()
        .AddSingleton<VectorIndexCache>()
        .AddSingleton<HybridSearchService>();
}
