using System.IO;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Storage;
using Assistant.Search;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>The application's container hands out the Windows Search service for the file search contract.</summary>
public sealed class FileSearchWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-ui-search-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    [Fact]
    public async Task TheContractIsTheWindowsSearchServiceReportedToTheActivityTracker_AndAnswers()
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

        var search = host.Services.GetRequiredService<IFileSearchService>();
        Assert.IsType<ActivityFileSearchService>(search);
        Assert.NotNull(host.Services.GetRequiredService<WindowsFileSearchService>());

        // Through the real index when this PC has one; otherwise the failure the contract promises, and nothing else.
        try
        {
            var results = await search.SearchAsync(new FileSearchQuery("test") { MaxResultsPerType = 2 });
            Assert.All(results, item => Assert.NotEqual(SearchResultItemType.App, item.Type));

            // The structured query and its capability flag reach the same service through the same wrapper.
            var plain = await search.SearchWithCapabilitiesAsync(new FileSearchQuery("test") { MaxResultsPerType = 2 });
            Assert.Equal(ContentSearchSupport.NotRequested, plain.ContentSearch.Support);

            var content = await search.SearchWithCapabilitiesAsync(new FileSearchQuery
            {
                ContentTerm = "test",
                Extensions = [".txt", ".zzqqxx"],
                Order = FileSearchOrder.ModifiedDescending,
                MaxResultsPerType = 2,
            });
            Assert.Equal(ContentSearchSupport.Partial, content.ContentSearch.Support);
            Assert.Equal([".zzqqxx"], content.ContentSearch.UnsupportedExtensions);
            Assert.All(content.Items, item => Assert.Null(item.Snippet));
        }
        catch (FileSearchException exception)
        {
            Assert.Equal(FileSearchFailure.IndexUnavailable, exception.Failure);
        }
    }
}
