using MCPIndexSearch.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MCPIndexSearch.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddMcpIndexInfrastructure(this IServiceCollection services, bool includeOcr)
    {
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<CpuUsageSettings>();
        services.AddSingleton<ICpuUsageSettings>(provider => provider.GetRequiredService<CpuUsageSettings>());
        services.AddSingleton<IGlobalCpuBudget, GlobalCpuBudget>();
        services.AddSingleton<IEmbeddingGenerator, GraniteEmbeddingGenerator>();
        services.AddSingleton<GraniteModelInstaller>();
        if (includeOcr)
        {
            services.AddSingleton<IOcrEngine, PpOcrV6Engine>();
        }

        return services;
    }
}
