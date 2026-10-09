using Assistant.Core.Contracts;
using Assistant.Documents.Context;
using Assistant.Documents.OpenXml;
using Assistant.Documents.Pdf;
using Assistant.Documents.Text;
using Assistant.Documents.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents;

/// <summary>Registers the document readers.</summary>
public static class DocumentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers a reader for each file type the Assistant reads (PROJECT_SPEC §4.7) and <see cref="IDocumentReaderRegistry"/>
    /// over them. Logging must be registered too. Nothing is read until a reader is asked to read a file.
    /// </summary>
    public static IServiceCollection AddAssistantDocuments(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDocumentReader>(provider => new PlainTextDocumentReader(provider.GetRequiredService<ILogger<PlainTextDocumentReader>>()));
        services.AddSingleton<IDocumentReader>(provider => new MarkdownDocumentReader(provider.GetRequiredService<ILogger<MarkdownDocumentReader>>()));
        services.AddSingleton<IDocumentReader>(provider => new PdfDocumentReader(provider.GetRequiredService<ILogger<PdfDocumentReader>>()));
        services.AddSingleton<IDocumentReader>(provider => new DocxDocumentReader(provider.GetRequiredService<ILogger<DocxDocumentReader>>()));
        services.AddSingleton<IDocumentReader>(provider => new PptxDocumentReader(provider.GetRequiredService<ILogger<PptxDocumentReader>>()));
        services.AddSingleton<IDocumentReader>(provider => new WebPageDocumentReader(provider.GetRequiredService<ILogger<WebPageDocumentReader>>()));
        services.AddSingleton<IDocumentReaderRegistry>(provider => new DocumentReaderRegistry(
            provider.GetServices<IDocumentReader>(), provider.GetRequiredService<ILogger<DocumentReaderRegistry>>()));

        // What a question about one file is given of it: the text read, cut into passages, and the passages that the question's
        // words point to (keyword ranking; no embeddings, no index).
        services.AddSingleton<IPassageSelector, LexicalPassageSelector>();
        services.AddSingleton<DocumentContextBuilder>(provider => new DocumentContextBuilder(provider.GetRequiredService<IPassageSelector>()));
        services.AddSingleton<DocumentContextService>(provider => new DocumentContextService(
            provider.GetRequiredService<IDocumentReaderRegistry>(),
            provider.GetRequiredService<DocumentContextBuilder>(),
            provider.GetRequiredService<ILogger<DocumentContextService>>()));
        services.AddSingleton<IDocumentContextService>(provider => provider.GetRequiredService<DocumentContextService>());
        return services;
    }
}
