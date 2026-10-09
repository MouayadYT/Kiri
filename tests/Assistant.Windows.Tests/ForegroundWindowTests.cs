using Assistant.Windows.Foreground;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Giving the keyboard to one of the app's windows (Ask Selection, PROJECT_SPEC §4.5). What it does to a real window in front of the user
/// is checked on the real PC when the shortcut is built; these only pin what it refuses to do, without touching the user's windows.
/// </summary>
public sealed class ForegroundWindowTests
{
    [Fact]
    public void NoWindow_IsNeverInFront_AndCannotBeBroughtThere()
    {
        Assert.False(ForegroundWindow.IsForeground(0));
        Assert.False(ForegroundWindow.TryActivate(0));
    }

    [Fact]
    public void AWindowThatDoesNotExist_IsNotInFront_AndTheRequestForItDoesNotThrow()
    {
        // A handle that no window has: Windows refuses, and the answer is a plain false.
        const nint NoSuchWindow = 0x0DEADBEE;

        Assert.False(ForegroundWindow.IsForeground(NoSuchWindow));
        Assert.False(ForegroundWindow.TryActivate(NoSuchWindow));
    }
}
