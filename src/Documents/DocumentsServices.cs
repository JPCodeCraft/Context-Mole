using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Documents;

public static class DocumentsServices
{
    public static IServiceCollection AddContextMoleDocuments(this IServiceCollection services)
    {
        services.AddSingleton<ContextMole.Core.IDocumentExtractor, DocumentExtractionRegistry>();
        services.AddSingleton<ContextMole.Core.IContentMaterializer, ContentMaterializationService>();
        return services;
    }
}