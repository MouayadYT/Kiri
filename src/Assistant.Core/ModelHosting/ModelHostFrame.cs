namespace Assistant.Core.ModelHosting;

/// <summary>One frame read from a model-host connection: a message, or why it could not be read.</summary>
public sealed record ModelHostFrame
{
    private ModelHostFrame(long id, ModelHostMessage? message, ModelHostErrorCode? error)
    {
        Id = id;
        Message = message;
        Error = error;
    }

    /// <summary>
    /// The request id the frame carries, or 0 when it has none or it could not be read. A reply to the frame carries
    /// it back.
    /// </summary>
    public long Id { get; }

    /// <summary>The message, or <see langword="null"/> when the frame could not be read.</summary>
    public ModelHostMessage? Message { get; }

    /// <summary>Why the frame could not be read, or <see langword="null"/> when it holds a <see cref="Message"/>.</summary>
    public ModelHostErrorCode? Error { get; }

    /// <summary>Creates a frame that holds a message.</summary>
    public static ModelHostFrame ForMessage(long id, ModelHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(id, message, null);
    }

    /// <summary>Creates a frame that could not be read, to be answered with a <see cref="ModelHostError"/>.</summary>
    public static ModelHostFrame ForError(long id, ModelHostErrorCode error) => new(id, null, error);
}
