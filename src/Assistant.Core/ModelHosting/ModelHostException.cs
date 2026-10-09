namespace Assistant.Core.ModelHosting;

/// <summary>
/// A request to the model host failed: the host answered with an error, or it could not be reached. The message is
/// always one of the fixed texts below, never content.
/// </summary>
public sealed class ModelHostException : Exception
{
    /// <summary>Creates the exception for an error the host answered with, or one found in its reply.</summary>
    public ModelHostException(ModelHostErrorCode code)
        : base($"The model host answered with an error ({code}).")
    {
        Code = code;
    }

    /// <summary>
    /// Creates the exception for an error the app found itself before asking the host, such as a model that is not set
    /// up.
    /// </summary>
    /// <param name="code">What went wrong, as the host would have said it.</param>
    /// <param name="message">A fixed, content-free description.</param>
    public ModelHostException(ModelHostErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates the exception for a host that could not be started or reached.</summary>
    /// <param name="message">A fixed, content-free description.</param>
    /// <param name="innerException">The failure behind it, if any.</param>
    public ModelHostException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>The host's error, or <see langword="null"/> when the host could not be started or reached.</summary>
    public ModelHostErrorCode? Code { get; }
}
