using System.Security.Cryptography;

namespace Assistant.Core.ImageSearch;

/// <summary>An image search provider was asked to search without the user's explicit say-so, or with a say-so that is spent, stale or for another picture.</summary>
public sealed class ImageSearchNotConfirmedException : InvalidOperationException
{
    /// <summary>Creates the exception. Its message holds nothing private.</summary>
    public ImageSearchNotConfirmedException()
        : base("The picture was not confirmed for sending.")
    {
    }
}

/// <summary>An image search could not be done by the provider: no connection, an error, an answer that could not be read. It holds nothing private.</summary>
public sealed class ImageSearchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ImageSearchException()
        : base("The image search could not be done.")
    {
    }

    /// <summary>Creates the exception with the provider's own.</summary>
    public ImageSearchException(Exception innerException)
        : base("The image search could not be done.", innerException)
    {
    }
}

/// <summary>
/// The user's say-so for one picture to be searched with (PROJECT_SPEC §3.4, §4.6): what lets a provider send it anywhere. It is issued
/// only by <see cref="ImageSearchFlow"/>, after the user has said yes to sending that picture to that provider, and a provider that
/// sends a picture off this PC must <see cref="Consume"/> it first. It holds one picture (by its hash), is good for one search, and
/// goes stale in <see cref="Lifetime"/>: so no code path can send a captured picture without the user having asked, and a say-so for
/// one picture cannot be used for another or twice.
/// </summary>
public sealed class ImageSearchConsent
{
    /// <summary>How long after it was given a say-so can still be used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly byte[] _hash;
    private int _used;

    private ImageSearchConsent(byte[] hash, DateTimeOffset issuedAt, bool userConfirmed)
    {
        _hash = hash;
        IssuedAt = issuedAt;
        UserConfirmed = userConfirmed;
    }

    /// <summary>When it was given.</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>Whether the user said yes to sending the picture: false for a provider that sends nothing off this PC.</summary>
    public bool UserConfirmed { get; }

    /// <summary>Gives a say-so for <paramref name="image"/>. Only the flow that asked the user does.</summary>
    internal static ImageSearchConsent Issue(ReadOnlyMemory<byte> image, DateTimeOffset now, bool userConfirmed) =>
        new(SHA256.HashData(image.Span), now, userConfirmed);

    /// <summary>
    /// Uses the say-so for <paramref name="image"/>, which a provider does before it sends anything. It is spent by this.
    /// </summary>
    /// <param name="image">The picture about to be sent.</param>
    /// <param name="now">The time now.</param>
    /// <param name="sendsImageOffPc">Whether the provider sends the picture off this PC: then the user must have said yes.</param>
    /// <exception cref="ImageSearchNotConfirmedException">
    /// The say-so was used before, is stale, is for another picture, or the user did not say yes to a send that leaves the PC.
    /// </exception>
    public void Consume(ReadOnlyMemory<byte> image, DateTimeOffset now, bool sendsImageOffPc)
    {
        if (Interlocked.Exchange(ref _used, 1) == 1
            || now - IssuedAt > Lifetime
            || now < IssuedAt
            || (sendsImageOffPc && !UserConfirmed)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(image.Span), _hash))
        {
            throw new ImageSearchNotConfirmedException();
        }
    }
}

/// <summary>A picture to search with, and the user's say-so to send it.</summary>
/// <param name="Image">The picture, encoded such as a PNG: only that picture is sent.</param>
/// <param name="Consent">The say-so for it, which the provider consumes before it sends anything.</param>
public sealed record ImageSearchRequest(ReadOnlyMemory<byte> Image, ImageSearchConsent Consent)
{
    // The picture is private content (PROJECT_SPEC §3.2).
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"ImageBytes = {Image.Length}");
        return true;
    }
}
