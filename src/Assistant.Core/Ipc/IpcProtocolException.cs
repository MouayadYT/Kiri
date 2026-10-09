namespace Assistant.Core.Ipc;

/// <summary>
/// The peer broke the framing of an IPC connection, so the connection cannot continue. The message never holds
/// content from the stream.
/// </summary>
public sealed class IpcProtocolException : IOException
{
    /// <summary>Creates the exception with a fixed, content-free message.</summary>
    public IpcProtocolException(string message)
        : base(message)
    {
    }
}
