using Assistant.Core.Contracts;
using Assistant.UI.Capture;
using Assistant.UI.ImageSearch;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>What the sample commands <c>demo capture</c> and <c>demo results</c> start (PROJECT_SPEC §4.6).</summary>
internal interface IVisualIntelligenceDemo
{
    /// <summary>Starts Visual Intelligence, as its shortcut does.</summary>
    void StartCapture();

    /// <summary>Shows made-up image search results in the results window; no search is made and nothing is sent.</summary>
    void ShowSampleResults();
}

/// <summary>
/// The sample commands' way into Visual Intelligence. It finds what it needs when a command is used, not when it is built: the
/// answer provider is made before the conversation that the capture opens into, so asking for them up front would loop.
/// </summary>
internal sealed class VisualIntelligenceDemo(IServiceProvider services) : IVisualIntelligenceDemo
{
    /// <inheritdoc/>
    public void StartCapture() => _ = services.GetRequiredService<VisualIntelligenceController>().InvokeAsync();

    /// <inheritdoc/>
    public void ShowSampleResults() =>
        services.GetRequiredService<IImageSearchResultsWindow>()
            .ShowResults(new ImageSearchResultsViewModel(PlaceholderImageSearchService.CreateSample(), services.GetRequiredService<IUrlLauncher>()));
}
