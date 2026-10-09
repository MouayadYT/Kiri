namespace Assistant.Core.Imaging;

/// <summary>
/// An image could not be prepared for the model: its bytes are not an image Windows can decode, or they are damaged.
/// The message never holds content or a path, and the inner exception is the decoder's own.
/// </summary>
public sealed class ImagePreprocessingException : Exception
{
    /// <summary>Creates the exception with a generic message.</summary>
    public ImagePreprocessingException()
        : this("The image could not be read.")
    {
    }

    /// <summary>Creates the exception with a message that holds no content or path.</summary>
    public ImagePreprocessingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message that holds no content or path, and the decoder's exception.</summary>
    public ImagePreprocessingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
