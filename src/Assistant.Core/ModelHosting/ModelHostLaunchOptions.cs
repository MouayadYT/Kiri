using System.Text;

namespace Assistant.Core.ModelHosting;

/// <summary>How the app starts and stops its model host (<see cref="ModelHostProcess"/>).</summary>
/// <param name="ExecutablePath">
/// The fully qualified path of <see cref="ModelHostProcess.ExecutableName"/>, which ships next to the app's own
/// executable.
/// </param>
public sealed record ModelHostLaunchOptions(string ExecutablePath)
{
    /// <summary>How long the host may take to start and answer the first ping.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the host may take to exit when asked, before it is ended.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Options for the host executable in <paramref name="directory"/>, usually the app's own directory.</summary>
    public static ModelHostLaunchOptions InDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new(Path.Combine(directory, ModelHostProcess.ExecutableName));
    }

    // Keeps the install path (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"StartTimeout = {StartTimeout}, ShutdownTimeout = {ShutdownTimeout}");
        return true;
    }
}
