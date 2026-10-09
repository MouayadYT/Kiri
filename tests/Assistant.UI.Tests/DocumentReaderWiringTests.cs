using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Storage;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>The application's container hands out the real document reader registry, not a placeholder.</summary>
public sealed class DocumentReaderWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-ui-documents-tests", Guid.NewGuid().ToString("N"));

    public DocumentReaderWiringTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    [Fact]
    public async Task TheRegistryReadsEveryKindOfDocumentAndFallsBackToUnsupportedForAnyOther()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        using var host = builder.Build();

        var registry = host.Services.GetRequiredService<IDocumentReaderRegistry>();
        Assert.Same(registry, host.Services.GetRequiredService<IDocumentReaderRegistry>());
        Assert.Equal(DocumentFileTypes.Extensions.Order(StringComparer.Ordinal), registry.SupportedExtensions);
        Assert.All([".docx", ".markdown", ".md", ".pdf", ".pptx", ".txt", ".html", ".mhtml", ".csv", ".json"], extension => Assert.Contains(extension, registry.SupportedExtensions));

        var text = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(text, "hello from a text file");
        var result = await registry.GetReader(text).ReadAsync(text);
        Assert.Equal(DocumentReadStatus.Success, result.Status);
        Assert.Equal("hello from a text file", Assert.Single(result.Segments).Text);

        var other = Path.Combine(_root, "program.exe");
        await File.WriteAllBytesAsync(other, [0x4D, 0x5A, 0x90, 0x00]);
        var unsupported = await registry.GetReader(other).ReadAsync(other);
        Assert.Equal(DocumentReadStatus.Unsupported, unsupported.Status);
        Assert.Empty(unsupported.Segments);
    }
}
