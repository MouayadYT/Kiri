using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Assistant.Core.Ipc;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// The model host's command line, written by <see cref="ModelHostProcess"/> and read by the host:
/// <c>--pipe &lt;name&gt; --owner &lt;process id&gt;</c>.
/// </summary>
/// <param name="PipeName">The pipe the host creates and serves, a name picked for this launch.</param>
/// <param name="OwnerProcessId">The app process that started the host. The host exits when it does.</param>
public sealed record ModelHostArguments(string PipeName, int OwnerProcessId)
{
    /// <summary>The option that names the pipe.</summary>
    public const string PipeOption = "--pipe";

    /// <summary>The option that names the owner process.</summary>
    public const string OwnerOption = "--owner";

    /// <summary>The command-line arguments, in order.</summary>
    public IReadOnlyList<string> ToCommandLine() =>
        [PipeOption, PipeName, OwnerOption, OwnerProcessId.ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Reads the arguments. Both options are required, each once, and nothing else is accepted, so a host started any
    /// other way (for example by opening its file) refuses to run.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out ModelHostArguments? arguments)
    {
        ArgumentNullException.ThrowIfNull(args);
        arguments = null;
        if (args.Count != 4)
        {
            return false;
        }

        string? pipeName = null;
        int? owner = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case PipeOption when pipeName is null && LocalPipe.IsValidName(value):
                    pipeName = value;
                    break;
                case OwnerOption when owner is null
                    && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    && id > 0:
                    owner = id;
                    break;
                default:
                    return false;
            }
        }

        if (pipeName is null || owner is null)
        {
            return false;
        }

        arguments = new ModelHostArguments(pipeName, owner.Value);
        return true;
    }
}
