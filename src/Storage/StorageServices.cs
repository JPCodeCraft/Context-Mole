using MCPIndexSearch.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.Storage;

public static class StorageServices
{
    public static IServiceCollection AddWritableMcpIndexStorage(this IServiceCollection services)
    {
        services.AddSingleton<SqliteSearchStore>();
        services.AddSingleton<ISearchStore>(provider => provider.GetRequiredService<SqliteSearchStore>());
        services.AddSingleton<DatabaseWriterService>();
        services.AddSingleton<IIndexWriter>(provider => provider.GetRequiredService<DatabaseWriterService>());
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseWriterService>());
        return services;
    }

    public static IServiceCollection AddReadOnlyMcpIndexStorage(this IServiceCollection services)
    {
        services.AddSingleton<ISearchStore, SqliteSearchStore>();
        return services;
    }
}
