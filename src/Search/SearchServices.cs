using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Search;

public static class SearchServices
{
    public static IServiceCollection AddContextMoleSearch(this IServiceCollection services,
        long vectorCacheByteBudget = VectorIndexCache.DefaultByteBudget, TimeSpan? recentSnapshotReuse = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorCacheByteBudget);
        return services
            .AddSingleton<IVectorIndexFactory, FlatVectorIndexFactory>()
            .AddSingleton(_ => new VectorIndexCache(vectorCacheByteBudget, recentSnapshotReuse))
            .AddSingleton(provider => new HybridSearchService(
                provider.GetRequiredService<ISearchStore>(),
                provider.GetRequiredService<IEmbeddingGenerator>(),
                provider.GetRequiredService<IVectorIndexFactory>(),
                provider.GetRequiredService<VectorIndexCache>(),
                provider.GetRequiredService<IGlobalCpuBudget>(),
                anchorSemanticPreviews: true));
    }
}
