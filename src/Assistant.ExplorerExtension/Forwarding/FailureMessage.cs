using Assistant.Core.Ipc;

namespace Assistant.ExplorerExtension.Forwarding;

/// <summary>
/// Tells the user, in a small message box, that Ask Assistant did not reach the app, since this process has no window of its
/// own. File Explorer starts one process for each selected file, so only one of them shows the message at a time.
/// </summary>
internal static class FailureMessage
{
    private const string Caption = "Assistant";
    private const string ShowingLockName = @"Local\Assistant.ExplorerExtension.Message";

    /// <summary>What the message says for <paramref name="result"/>, or <see langword="null"/> when nothing went wrong.</summary>
    public static string? For(ForwardResult result) => result switch
    {
        ForwardResult.Forwarded => null,
        ForwardResult.AppNotFound =>
            "The Assistant couldn't be found where its File Explorer entry expects it. Start the Assistant once so it can update the entry, then try again.",
        ForwardResult.Refused =>
            "The Assistant couldn't take these files. If it was just updated, restart it, then try again.",
        _ => "The Assistant didn't respond. Make sure it's running, then try Ask Assistant again.",
    };

    /// <summary>Shows the message for <paramref name="result"/>, unless another process is showing one.</summary>
    public static void Show(ForwardResult result)
    {
        if (For(result) is not { } text)
        {
            return;
        }

        using var showing = new Mutex(initiallyOwned: true, ShowingLockName, out var first);
        if (!first)
        {
            return;
        }

        try
        {
            NativeMethods.MessageBox(0, text, Caption, NativeMethods.MessageBoxInformation);
        }
        finally
        {
            showing.ReleaseMutex();
        }
    }
}
