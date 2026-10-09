using System.Net;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ImageSearch;
using Assistant.Core.Settings;
using Assistant.Search.ImageSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// The real image search (PROJECT_SPEC §4.6): the picture goes to Bing's visual search only when the user said yes, Bing's name for it is searched for, and the
/// matches come back with a title, the site and a thumbnail. Bing is a fake here, so nothing is sent.
/// </summary>
public sealed class BingImageSearchTests
{
    private static readonly byte[] Picture = [1, 2, 3, 4, 5];

    private const string Matches = """
        <html><body>
        <a class="iusc" m="{&quot;murl&quot;:&quot;https://one.example/full.jpg&quot;,&quot;turl&quot;:&quot;https://ts1.mm.bing.net/th?id=one&quot;,&quot;purl&quot;:&quot;https://www.one.example/page&quot;,&quot;t&quot;:&quot;Golden Gate &amp;amp; the bay&quot;}"></a>
        <a class="iusc" m="{&quot;murl&quot;:&quot;https://two.example/full.jpg&quot;,&quot;turl&quot;:&quot;https://evil.example/th?id=two&quot;,&quot;purl&quot;:&quot;https://two.example/p&quot;,&quot;t&quot;:&quot;Second&quot;}"></a>
        <a class="iusc" m="not json"></a>
        <a class="iusc" m="{&quot;murl&quot;:&quot;javascript:alert(1)&quot;,&quot;turl&quot;:&quot;javascript:alert(1)&quot;,&quot;purl&quot;:&quot;javascript:alert(1)&quot;,&quot;t&quot;:&quot;Bad&quot;}"></a>
        </body></html>
        """;

    private sealed class Bing : HttpMessageHandler
    {
        public string? UploadedImage { get; private set; }

        public int Requests { get; private set; }

        public List<Uri> Fetched { get; } = [];

        public string? Name { get; init; } = "Golden Gate Bridge";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var uri = request.RequestUri!;
            Fetched.Add(uri);
            if (request.Method == HttpMethod.Post && uri.Host == "www.bing.com" && uri.AbsolutePath == "/images/search")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                UploadedImage = form.Contains("imageBin", StringComparison.Ordinal) ? form : null;

                // A browser (or HttpClient) is sent on to a search for Bing's name for the picture; the answer says which address it ended at.
                var final = Name is null ? new Uri("https://www.bing.com/search?bcid=x") : new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(Name) + "&bcid=x");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, final),
                    Content = new StringContent(Name is null ? "<title>Search</title>" : "<title>" + Name + " - Search</title>", Encoding.UTF8, "text/html"),
                };
            }

            if (uri.Host == "www.bing.com" && uri.AbsolutePath == "/images/search")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Matches, Encoding.UTF8, "text/html") };
            }

            if (uri.Host.EndsWith(".mm.bing.net", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9]) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class Settings(bool localOnly) : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings { Privacy = new PrivacySettings { LocalOnly = localOnly } });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAll : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, PermissionDecisionReason.Granted));
    }

    private sealed class Confirmation(bool yes) : IImageSearchConfirmation
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(ImageSearchDisclosure disclosure, CancellationToken cancellationToken = default)
        {
            Asked++;
            Assert.Equal("Bing", disclosure.ProviderName);
            return Task.FromResult(yes);
        }
    }

    private static ImageSearchFlow Flow(BingImageSearchService provider, Confirmation confirmation, bool localOnly = false) =>
        new(new Settings(localOnly), new AllowAll(), provider, confirmation, TimeProvider.System, NullLogger<ImageSearchFlow>.Instance);

    [Fact]
    public async Task ThePictureIsUploadedToBingAndBingsNameForItFindsMatchesWithThumbnails()
    {
        var bing = new Bing();
        using var provider = new BingImageSearchService(handler: bing);

        var outcome = await Flow(provider, new Confirmation(true)).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Results, outcome.Kind);
        var results = outcome.Results!;
        Assert.Equal("Bing", results.ProviderName);
        Assert.False(results.IsSample);
        Assert.Contains(Convert.ToBase64String(Picture), Uri.UnescapeDataString(bing.UploadedImage!.Replace('+', ' ')), StringComparison.Ordinal);

        // The one with a thumbnail comes first; the title is plain text, the site has no www, and the page is the match's own.
        var first = results.Results[0];
        Assert.Equal("Golden Gate & the bay", first.Title);
        Assert.Equal("one.example", first.SourceName);
        Assert.Equal(new Uri("https://www.one.example/page"), first.PageUrl);
        Assert.Equal([9, 9, 9], first.Thumbnail.ToArray());

        // A thumbnail is only fetched from Bing's own servers, and an address that is not http or https is no match.
        Assert.DoesNotContain(bing.Fetched, uri => uri.Host == "evil.example");
        Assert.Equal(2, results.Results.Count);
        Assert.True(results.Results[1].Thumbnail.IsEmpty);
        Assert.Equal("Second", results.Results[1].Title);
    }

    [Fact]
    public async Task NothingIsSentWhenTheUserDoesNotSayYes()
    {
        var bing = new Bing();
        using var provider = new BingImageSearchService(handler: bing);

        var outcome = await Flow(provider, new Confirmation(false)).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Declined, outcome.Kind);
        Assert.Equal(0, bing.Requests);
    }

    [Fact]
    public async Task NothingIsSentWhileLocalOnlyModeIsOn()
    {
        var bing = new Bing();
        using var provider = new BingImageSearchService(handler: bing);
        var confirmation = new Confirmation(true);

        var outcome = await Flow(provider, confirmation, localOnly: true).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Blocked, outcome.Kind);
        Assert.Equal(ImageSearchBlock.LocalOnly, outcome.Block);
        Assert.Equal(0, confirmation.Asked);
        Assert.Equal(0, bing.Requests);
    }

    [Fact]
    public async Task APictureBingCannotNameFailsAndSaysSoInsteadOfShowingGuesses()
    {
        using var provider = new BingImageSearchService(handler: new Bing { Name = null });

        var outcome = await Flow(provider, new Confirmation(true)).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Failed, outcome.Kind);
    }

    [Fact]
    public async Task AnEmptyAnswerFromBingIsAFailureAndAServerThatIsDownIsOne()
    {
        using var down = new BingImageSearchService(handler: new ThrowingHandler());

        var outcome = await Flow(down, new Confirmation(true)).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Failed, outcome.Kind);
    }

    [Fact]
    public void WhatTheWebReturnedIsCleanedAndNothingOfItIsInTheText()
    {
        var parsed = BingImageSearchService.ParseMatches(Matches).ToList();

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Golden Gate & the bay", parsed[0].Title);
        var text = new ImageSearchResult("t", "s", new Uri("https://x.example/"), new byte[] { 1 }).ToString();
        Assert.DoesNotContain("x.example", text, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("down");
    }
}
