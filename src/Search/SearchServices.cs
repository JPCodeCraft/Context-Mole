using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Search;

public static class SearchServices
{
    public static IServiceCollection AddContextMoleSearch(this IServiceCollection services) => services
        .AddSingleton<IVectorIndexFactory, FlatVectorIndexFactory>()
        .AddSingleton<VectorIndexCache>()
        .AddSingleton<HybridSearchService>();
}