namespace Assistant.Core.Documents;

/// <summary>
/// How reading a document ended (PROJECT_SPEC §4.7). A document that cannot be read is a status, never an exception, so a
/// caller handles every outcome in one place; only cancelling is an exception.
/// </summary>
public enum DocumentReadStatus
{
    /// <summary>The file was read and has text (or, for a metadata request, its properties were read).</summary>
    Success,

    /// <summary>The file is sound and holds no text to extract: a scanned PDF with no text layer, an empty file, slides of pictures.</summary>
    NoText,

    /// <summary>
    /// No reader handles this kind of file, or the content is not what a reader reads (binary data under a <c>.txt</c> name).
    /// Nothing was parsed: an unknown binary format is never guessed at.
    /// </summary>
    Unsupported,

    /// <summary>There is no such file (or the path is not a full path to a file).</summary>
    NotFound,

    /// <summary>The file is larger than <see cref="DocumentReadOptions.MaxFileBytes"/>, or what it unpacks to is larger than the limits allow.</summary>
    TooLarge,

    /// <summary>The file is protected by a password or encryption the Assistant does not have a key for.</summary>
    Encrypted,

    /// <summary>The file is not a valid document of the type its name says, or it is damaged.</summary>
    Corrupt,

    /// <summary>The file exists but cannot be read: access denied, locked by another program, an I/O error.</summary>
    Unreadable,

    /// <summary>The Files permission does not allow reading it (step 119). The file was not opened.</summary>
    NotAllowed,
}
