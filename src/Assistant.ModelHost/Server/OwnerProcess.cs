using System.ComponentModel;
using System.Diagnostics;

namespace Assistant.ModelHost.Server;

/// <summary>Watches the app process that owns the host, so the host never outlives it.</summary>
internal static class OwnerProcess
{
    /// <summary>
    /// Completes when the owner has exited, at once when it is not running. It never completes when there is no owner,
    /// or when the owner cannot be watched; the connection then decides when the host exits.
    /// </summary>
    public static async Task WaitForExitAsync(int? processId, CancellationToken cancellationToken)
    {
        if (processId is not { } id)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }

        Process owner;
        try
        {
            owner = Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            // No process has that id: the owner has already exited.
            return;
        }

        using (owner)
        {
            try
            {
                await owner.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
