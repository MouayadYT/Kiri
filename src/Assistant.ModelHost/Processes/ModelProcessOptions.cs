using System.Text;

namespace Assistant.ModelHost.Processes;

/// <summary>Where the model engine listens, how long it may take to start, and how it is restarted.</summary>
/// <param name="SocketDirectory">
/// The folder for the engine's socket, <c>AppPaths.SocketsDirectory</c> in the app. It must be short: a socket's full
/// path must fit in <see cref="MaxSocketPathBytes"/> bytes.
/// </param>
internal sealed record ModelProcessOptions(string SocketDirectory)
{
    /// <summary>The longest socket path Windows accepts, in UTF-8 bytes (<c>sun_path</c> less its terminator).</summary>
    public const int MaxSocketPathBytes = 107;

    /// <summary>How long the engine may take to load its model and start listening, each time it is started.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How the engine is restarted after it exits unexpectedly.</summary>
    public ModelProcessRestartPolicy RestartPolicy { get; init; } = new();

    // Keeps the user's profile path (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"StartTimeout = {StartTimeout}, RestartPolicy = {RestartPolicy}");
        return true;
    }
}
