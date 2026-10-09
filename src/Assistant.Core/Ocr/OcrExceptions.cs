namespace Assistant.Core.Ocr;

/// <summary>No OCR language is installed on this PC, so no text can be read from a picture. The message holds nothing private.</summary>
public sealed class OcrUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OcrUnavailableException()
        : base("No OCR language is installed on this PC.")
    {
    }
}

/// <summary>A picture could not be read for its text. The message holds nothing private; the inner exception is the engine's own.</summary>
public sealed class OcrFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OcrFailedException()
        : base("The picture could not be read for its text.")
    {
    }

    /// <summary>Creates the exception with the engine's own.</summary>
    public OcrFailedException(Exception innerException)
        : base("The picture could not be read for its text.", innerException)
    {
    }
}
