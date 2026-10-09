using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Contracts;
using Assistant.Core.ImageSearch;

namespace Assistant.Search.ImageSearch;

/// <summary>
/// The Assistant's image search provider (PROJECT_SPEC §4.6): Bing's visual search, which needs no account and no key. The picture the user selected, and
/// confirmed for sending, is uploaded to <c>bing.com</c> (and nowhere else); Bing answers with its best name for what the picture shows, and the Assistant
/// then asks Bing for pictures of that, so the user sees matches with a thumbnail, a title and the site each is on. A click on a match opens its page in
/// the user's browser. Only <c>bing.com</c> and the <c>mm.bing.net</c> thumbnail servers are ever spoken to, over https, with no cookies kept; nothing
/// the web returned is logged. When Bing gives no name for the picture the search fails with <see cref="ImageSearchException"/> and says so.
/// </summary>
public sealed partial class BingImageSearchService : IImageSearchService, IDisposable
{
    /// <summary>The provider's name, which the results are headed with.</summary>
    public const string Name = "Bing";

    /// <summary>The most matches shown.</summary>
    public const int MaxResults = 18;

    private const int MaxUploadBytes = 3 * 1024 * 1024;
    private const int MaxThumbnailBytes = 400 * 1024;
    private const int MaxPageBytes = 6 * 1024 * 1024;
    private const int ThumbnailsAtOnce = 6;
    private static readonly Uri UploadAddress = new("https://www.bing.com/images/search?view=detailv2&iss=sbiupload&FORM=SBIHMP&sbisrc=ImgDropper&q=imgurl:");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IImagePreprocessor? _images;
    private readonly TimeProvider _clock;

    /// <summary>Creates the provider.</summary>
    /// <param name="images">Makes a large picture small enough to upload; without it a picture over 3 MB is not searched.</param>
    /// <param name="clock">The time, for the say-so.</param>
    /// <param name="handler">The way requests are sent, for tests; a plain one that follows redirects and keeps no cookies by default.</param>
    public BingImageSearchService(IImagePreprocessor? images = null, TimeProvider? clock = null, HttpMessageHandler? handler = null)
    {
        _images = images;
        _clock = clock ?? TimeProvider.System;
        _ownsHttp = true;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <inheritdoc/>
    public string ProviderName => Name;

    /// <inheritdoc/>
    public bool IsSample => false;

    /// <inheritdoc/>
    public async Task<ImageSearchResults> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The picture leaves this PC only after the user said yes to this picture.
        request.Consent.Consume(request.Image, _clock.GetUtcNow(), sendsImageOffPc: true);
        try
        {
            var upload = await PrepareAsync(request.Image, cancellationToken).ConfigureAwait(false);
            var name = await NameAsync(upload, cancellationToken).ConfigureAwait(false)
                ?? throw new ImageSearchException();
            var matches = await MatchesAsync(name, cancellationToken).ConfigureAwait(false);
            return new ImageSearchResults(Name, IsSample: false, matches);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException
            && !cancellationToken.IsCancellationRequested)
        {
            throw new ImageSearchException(exception);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<ReadOnlyMemory<byte>> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        if (_images is not null)
        {
            try
            {
                var prepared = await _images.PrepareAsync(image, cancellationToken).ConfigureAwait(false);
                if (prepared.Data.Length <= MaxUploadBytes)
                {
                    return prepared.Data;
                }
            }
            catch (Assistant.Core.Imaging.ImagePreprocessingException)
            {
                // What cannot be read as a picture is sent as it is, if it is small enough, and Bing says what it makes of it.
            }
        }

        return image.Length <= MaxUploadBytes ? image : throw new ImageSearchException();
    }

    // Uploads the picture to Bing's visual search. Bing answers with a redirect to a search for its best name for what the picture shows, so the name is in the address
    // it ends at (and in the page's title).
    internal async Task<string?> NameAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent { { new StringContent(Convert.ToBase64String(image.Span)), "imageBin" } };
        using var response = await _http.PostAsync(UploadAddress, form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri;
        if (final is null || !IsBing(final))
        {
            return null;
        }

        var name = QueryValue(final, "q");
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("imgurl:", StringComparison.OrdinalIgnoreCase))
        {
            var html = await ReadAsync(response, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            name = TitleName(html);
        }

        return Clean(name, 120) is { Length: > 0 } cleaned ? cleaned : null;
    }

    // The pictures Bing has for the name, with their pages and thumbnails.
    internal async Task<IReadOnlyList<ImageSearchResult>> MatchesAsync(string name, CancellationToken cancellationToken)
    {
        var address = new Uri("https://www.bing.com/images/search?form=HDRSC3&first=1&q=" + Uri.EscapeDataString(name));
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await ReadAsync(response, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        var entries = ParseMatches(html).Take(MaxResults).ToList();

        // The thumbnails come in a few at a time; a match whose thumbnail does not come is still listed, with its title.
        using var gate = new SemaphoreSlim(ThumbnailsAtOnce);
        var results = await Task.WhenAll(entries.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var thumbnail = await ThumbnailAsync(entry.Thumbnail, cancellationToken).ConfigureAwait(false);
                return new ImageSearchResult(entry.Title, entry.Site, entry.Page, thumbnail);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return results.Where(result => !result.Thumbnail.IsEmpty).Concat(results.Where(result => result.Thumbnail.IsEmpty)).ToList();
    }

    private async Task<ReadOnlyMemory<byte>> ThumbnailAsync(Uri? address, CancellationToken cancellationToken)
    {
        if (address is null || address.Scheme != Uri.UriSchemeHttps || !address.Host.EndsWith(".mm.bing.net", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxThumbnailBytes)
            {
                return default;
            }

            return await ReadBytesAsync(response, MaxThumbnailBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return default;
        }
    }

    // What a page lists as a match: the pictures' own data is in the "m" attribute of each result as JSON.
    internal static IEnumerable<(string Title, string Site, Uri? Page, Uri? Thumbnail)> ParseMatches(string html)
    {
        foreach (Match match in MatchPattern().Matches(html))
        {
            JsonElement data;
            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups[1].Value));
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            string? Text(string property) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            var page = WebAddress(Text("purl"));
            var thumbnail = WebAddress(Text("turl"));
            if (page is null && thumbnail is null)
            {
                continue;
            }

            var title = Clean(Text("t"), 140);
            var site = page is null ? "Bing" : page.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? page.Host[4..] : page.Host;
            yield return (string.IsNullOrEmpty(title) ? site : title, site, page, thumbnail);
        }
    }

    private static string? TitleName(string html)
    {
        var title = TitlePattern().Match(html);
        if (!title.Success)
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(title.Groups[1].Value);
        const string suffix = " - Search";
        return text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : null;
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(response, limit, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                throw new IOException("The answer is larger than expected.");
            }
        }

        return buffer.ToArray();
    }

    private static bool IsBing(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps && (address.Host == "bing.com" || address.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase));

    private static Uri? WebAddress(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" ? address : null;

    private static string? QueryValue(Uri address, string key)
    {
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(pair[..equals], key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    // One line of plain text: Bing marks matched words with private-use characters, and titles hold tags and entities.
    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var plain = TagPattern().Replace(WebUtility.HtmlDecode(text), " ");
        var line = new string([.. plain.Where(character => !char.IsControl(character) && character is not ('' or ''))]);
        line = WhitespacePattern().Replace(line, " ").Trim();
        return line.Length <= maxLength ? line : line[..maxLength].TrimEnd() + "…";
    }

    [GeneratedRegex("class=\"iusc\"[^>]*\\bm=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex MatchPattern();

    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
