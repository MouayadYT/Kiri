namespace Assistant.Core.Contracts;

/// <summary>Why a file search could not be answered.</summary>
public enum FileSearchFailure
{
    /// <summary>
    /// Windows Search is not there to ask: its service is disabled or stopped, or the index is not set up. The user can turn
    /// it on (PROJECT_SPEC §4.7); nothing else finds files instead.
    /// </summary>
    IndexUnavailable = 0,

    /// <summary>The index did not answer in time.</summary>
    TimedOut = 1,

    /// <summary>The index refused the query or failed while answering it.</summary>
    QueryFailed = 2,

    /// <summary>The Files permission does not allow searching (step 119). Windows Search was not asked.</summary>
    NotAllowed = 3,
}

/// <summary>
/// A file search failed. Neither the message nor anything else on it holds the query or a result, and it has no inner
/// exception, because a provider's own message can repeat a piece of the query. It keeps only the provider's error code.
/// </summary>
public sealed class FileSearchException : Exception
{
    /// <summary>Creates the exception for <paramref name="failure"/> with a generic message.</summary>
    public FileSearchException(FileSearchFailure failure, int? providerErrorCode = null)
        : this(failure, Describe(failure), providerErrorCode)
    {
    }

    /// <summary>Creates the exception with a message that holds no query and no result.</summary>
    public FileSearchException(FileSearchFailure failure, string message, int? providerErrorCode = null)
        : base(message)
    {
        Failure = failure;
        ProviderErrorCode = providerErrorCode;
    }

    /// <summary>Why the search failed.</summary>
    public FileSearchFailure Failure { get; }

    /// <summary>The error code (an HRESULT) the search provider gave, when it gave one.</summary>
    public int? ProviderErrorCode { get; }

    private static string Describe(FileSearchFailure failure) => failure switch
    {
        FileSearchFailure.IndexUnavailable => "Windows Search is not available.",
        FileSearchFailure.TimedOut => "Windows Search did not answer in time.",
        FileSearchFailure.NotAllowed => "Files is not allowed in Settings, under Permissions.",
        _ => "Windows Search could not run the search.",
    };
}
