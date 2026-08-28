using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Search;

public static class SearchServices
{
    public static IServiceCollection AddContextMoleSearch(this IServiceCollection services,
        long vectorCacheByteBudget = VectorIndexCache.DefaultByteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorCacheByteBudget);
        return services
            .AddSingleton<IVectorIndexFactory, FlatVectorIndexFactory>()
            .AddSingleton(_ => new VectorIndexCache(vectorCacheByteBudget))
            .AddSingleton<HybridSearchService>();
    }
}
