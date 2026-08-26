using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.Documents;

public static class DocumentsServices
{
    public static IServiceCollection AddMcpIndexDocuments(this IServiceCollection services)
    {
        services.AddSingleton<MCPIndexSearch.Core.IDocumentExtractor, DocumentExtractionRegistry>();
        services.AddSingleton<MCPIndexSearch.Core.IContentMaterializer, ContentMaterializationService>();
        return services;
    }
}
