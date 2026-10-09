using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.ImageSearch;

/// <summary>
/// The permission flow of an image search (PROJECT_SPEC §3.4, §4.6, §4.9): whether it can be done at all, and, when the picture would
/// leave this PC, the user's say-so for it. It is the only way a captured picture reaches an <see cref="IImageSearchService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Local Only mode must be off (<see cref="PrivacySettings.LocalOnly"/>, on by default): while it is on, nothing is searched. External
/// Web and Image Search must be allowed in the Permissions (<see cref="PermissionCapability.ExternalSearch"/>). A provider must be set
/// up; without a real one, the sample provider makes up results and sends nothing. And for a provider that sends the picture off this PC,
/// the user is shown the picture and the provider and must say yes, for each picture.
/// </para>
/// <para>The picture and the results are never logged; what is logged is that a search was blocked, declined, done or failed.</para>
/// </remarks>
public sealed partial class ImageSearchFlow(
    ISettingsService settings,
    IPermissionPolicy permissions,
    IImageSearchService? provider,
    IImageSearchConfirmation? confirmation,
    TimeProvider clock,
    ILogger<ImageSearchFlow>? logger = null,
    IPermissionGate? gate = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ImageSearchFlow>.Instance;

    /// <summary>Decides, for the settings as they are saved now, whether an image search can be done and with whom.</summary>
    public async Task<ImageSearchAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var saved = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var decision = await permissions.CheckAsync(PermissionCapability.ExternalSearch, cancellationToken).ConfigureAwait(false);
        // A permission set to ask every time counts as open here (step 119): the user is asked about each search itself, below, so the chip is offered.
        return Evaluate(saved.Privacy.LocalOnly, decision.CouldBeAllowed, provider);
    }

    /// <summary>
    /// The pure rule: Local Only first (nothing is searched in it, whatever else is set), then the permission, then whether there is a
    /// provider to search with.
    /// </summary>
    public static ImageSearchAvailability Evaluate(bool localOnly, bool permissionAllowed, IImageSearchService? provider)
    {
        var name = provider?.ProviderName ?? string.Empty;
        var sample = provider?.IsSample ?? false;
        if (localOnly)
        {
            return new ImageSearchAvailability(ImageSearchBlock.LocalOnly, name, sample);
        }

        if (!permissionAllowed)
        {
            return new ImageSearchAvailability(ImageSearchBlock.PermissionOff, name, sample);
        }

        return provider is null
            ? new ImageSearchAvailability(ImageSearchBlock.NoProvider, string.Empty, false)
            : new ImageSearchAvailability(ImageSearchBlock.None, name, sample);
    }

    /// <summary>
    /// Searches with <paramref name="image"/> for the user, if the rules allow, asking them first when the picture would leave this PC.
    /// Nothing is sent unless every rule holds and, for a provider that sends it, the user said yes to this picture.
    /// </summary>
    /// <param name="image">The picture, encoded such as a PNG: what the user selected, and nothing else.</param>
    /// <param name="cancellationToken">Gives the search up.</param>
    public async Task<ImageSearchOutcome> SearchAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        if (image.IsEmpty)
        {
            throw new ArgumentException("There is no picture to search with.", nameof(image));
        }

        var availability = await GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable || provider is null)
        {
            LogBlocked(_logger, availability.Block);
            return new ImageSearchOutcome(ImageSearchOutcomeKind.Blocked, Block: availability.Block);
        }

        var sends = provider.SendsImageOffPc;

        // Set to ask every time: the user is asked about this search. A picture that would be sent is asked about in the window that shows it, which is that question, so
        // it is asked once; a search that sends nothing (the sample) is asked about like any other use of the permission.
        if (!sends && (await permissions.CheckAsync(PermissionCapability.ExternalSearch, cancellationToken).ConfigureAwait(false)).NeedsAsking)
        {
            var grant = gate is null
                ? null
                : await gate.RequestAsync(PermissionCapability.ExternalSearch, "Search with the picture you selected.", cancellationToken).ConfigureAwait(false);
            if (grant is null || !grant.IsGranted)
            {
                LogDeclined(_logger);
                return new ImageSearchOutcome(ImageSearchOutcomeKind.Declined);
            }
        }

        if (sends)
        {
            // The picture leaves this PC only if the user, shown it and the provider's name, says yes.
            var said = confirmation is not null
                && await confirmation.ConfirmAsync(new ImageSearchDisclosure(provider.ProviderName, image), cancellationToken).ConfigureAwait(false);
            if (!said)
            {
                LogDeclined(_logger);
                return new ImageSearchOutcome(ImageSearchOutcomeKind.Declined);
            }
        }

        try
        {
            var results = await provider.SearchAsync(
                new ImageSearchRequest(image, ImageSearchConsent.Issue(image, clock.GetUtcNow(), userConfirmed: sends)), cancellationToken)
                .ConfigureAwait(false);
            LogSearched(_logger, results.IsSample, results.Results.Count);
            return new ImageSearchOutcome(ImageSearchOutcomeKind.Results, results);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(_logger, exception.GetType().Name);
            return new ImageSearchOutcome(ImageSearchOutcomeKind.Failed);
        }
    }

    [LoggerMessage(EventId = 2320, Level = LogLevel.Information, Message = "An image search was not done: {Block}")]
    private static partial void LogBlocked(ILogger logger, ImageSearchBlock block);

    [LoggerMessage(EventId = 2321, Level = LogLevel.Information, Message = "The user did not allow the picture to be sent, so no image search was done")]
    private static partial void LogDeclined(ILogger logger);

    [LoggerMessage(EventId = 2322, Level = LogLevel.Information, Message = "An image search was done: {IsSample} sample, {Results} results")]
    private static partial void LogSearched(ILogger logger, bool isSample, int results);

    [LoggerMessage(EventId = 2323, Level = LogLevel.Warning, Message = "An image search failed ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
