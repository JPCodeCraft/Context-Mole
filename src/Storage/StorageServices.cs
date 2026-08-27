using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Storage;

public static class StorageServices
{
    public static IServiceCollection AddWritableContextMoleStorage(this IServiceCollection services)
    {
        services.AddSingleton<SqliteSearchStore>();
        services.AddSingleton<ISearchStore>(provider => provider.GetRequiredService<SqliteSearchStore>());
        services.AddSingleton<DatabaseWriterService>();
        services.AddSingleton<IIndexWriter>(provider => provider.GetRequiredService<DatabaseWriterService>());
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseWriterService>());
        return services;
    }

    public static IServiceCollection AddReadOnlyContextMoleStorage(this IServiceCollection services)
    {
        services.AddSingleton<ISearchStore, SqliteSearchStore>();
        return services;
    }
}