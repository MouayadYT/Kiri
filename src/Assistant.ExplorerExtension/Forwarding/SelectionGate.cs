using System.Security.Cryptography;
using System.Text;

namespace Assistant.ExplorerExtension.Forwarding;

/// <summary>
/// Lets one of the processes File Explorer starts for one selection send it (PROJECT_SPEC §4.4). Each of them reads the whole
/// selection, so only the first to ask for a selection, and then for as long as it keeps the claim, sends it; the others have
/// nothing left to do.
/// </summary>
internal interface ISelectionGate
{
    /// <summary>
    /// Claims the selection of <paramref name="paths"/>, which is the same for every process started for it whatever their order
    /// of arrival. Returns the claim, to be disposed when it is no longer needed, or <see langword="null"/> when another process
    /// holds it.
    /// </summary>
    IDisposable? TryClaim(IReadOnlyList<string> paths);
}

/// <summary>A claim as a named mutex of this Windows session, named for the selection, that exists while the claimant keeps it.</summary>
internal sealed class NamedMutexSelectionGate : ISelectionGate
{
    private const string Prefix = @"Local\Assistant.ExplorerExtension.Selection.";

    /// <inheritdoc/>
    public IDisposable? TryClaim(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var mutex = new Mutex(initiallyOwned: false, NameFor(paths), out var created);
        if (created)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>The name of the claim for <paramref name="paths"/>: the same paths in any case and order give the same name.</summary>
    public static string NameFor(IReadOnlyList<string> paths)
    {
        var key = string.Join('\n', paths.Select(path => path.ToUpperInvariant()).Order(StringComparer.Ordinal));
        return Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8).ToLowerInvariant();
    }
}
