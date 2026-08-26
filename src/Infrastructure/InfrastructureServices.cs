using MCPIndexSearch.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddMcpIndexInfrastructure(this IServiceCollection services, bool includeOcr)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IEmbeddingGenerator, GraniteEmbeddingGenerator>();
        services.AddSingleton<GraniteModelInstaller>();
        if (includeOcr)
        {
            services.AddSingleton<IOcrEngine, PpOcrV6Engine>();
        }

        return services;
    }
}
