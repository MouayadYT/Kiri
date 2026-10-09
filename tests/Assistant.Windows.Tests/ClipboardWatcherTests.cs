using System.Runtime.InteropServices;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Windows.Clipboard;
using Assistant.Windows.Interop;
using Assistant.Windows.Selection;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// What the clipboard history's watcher keeps of a copy (PROJECT_SPEC §4.1): only text that an application has not marked as private,
/// within the history's limits. The rules are checked against a clipboard the test writes; the real window and the real clipboard are
/// checked only by a test that is off unless <c>ASSISTANT_TEST_CLIPBOARD=1</c>, because it uses the user's clipboard for a moment.
/// </summary>
public sealed class ClipboardWatcherTests
{
    private static readonly ClipboardHistoryLimits Limits = new(20, 10, TimeSpan.FromDays(1));

    [Fact]
    public void OrdinaryTextIsKeptAsItIsAndTheLimitPlusOneCharacterIsRead()
    {
        var clipboard = new FakeClipboard { Text = "hello" };

        Assert.Equal("hello", ClipboardTextFilter.TryRead(clipboard, Limits));
        Assert.Equal(Limits.MaxItemCharacters + 1, clipboard.AskedFor);
    }

    [Theory]
    [InlineData(ClipboardTextFilter.ExcludeFromMonitoring)]
    [InlineData(ClipboardTextFilter.ViewerIgnore)]
    public void TextAnApplicationMarkedAsNotToBeMonitoredIsNeverKept(string format)
    {
        var clipboard = new FakeClipboard { Text = "hunter2" };
        clipboard.Formats.Add(format);

        Assert.Null(ClipboardTextFilter.TryRead(clipboard, Limits));
        Assert.Equal(0, clipboard.TextReads);
    }

    [Theory]
    [InlineData(ClipboardTextFilter.CanIncludeInHistory)]
    [InlineData(ClipboardTextFilter.CanUploadToCloud)]
    public void TextMarkedAsNotForHistoryOrTheCloudIsNeverKept(string flag)
    {
        var clipboard = new FakeClipboard { Text = "hunter2" };
        clipboard.Flags[flag] = 0;

        Assert.Null(ClipboardTextFilter.TryRead(clipboard, Limits));
        Assert.Equal(0, clipboard.TextReads);
    }

    [Fact]
    public void TextMarkedAsFineForHistoryIsKept()
    {
        var clipboard = new FakeClipboard { Text = "ok" };
        clipboard.Flags[ClipboardTextFilter.CanIncludeInHistory] = 1;
        clipboard.Flags[ClipboardTextFilter.CanUploadToCloud] = 1;

        Assert.Equal("ok", ClipboardTextFilter.TryRead(clipboard, Limits));
    }

    [Fact]
    public void TextThatIsTooLongBlankOrNotTextIsNotKeptAndNothingIsCut()
    {
        Assert.Equal("1234567890", ClipboardTextFilter.TryRead(new FakeClipboard { Text = "1234567890" }, Limits));
        Assert.Null(ClipboardTextFilter.TryRead(new FakeClipboard { Text = "12345678901" }, Limits));
        Assert.Null(ClipboardTextFilter.TryRead(new FakeClipboard { Text = "  \r\n " }, Limits));
        Assert.Null(ClipboardTextFilter.TryRead(new FakeClipboard { Text = "" }, Limits));
        Assert.Null(ClipboardTextFilter.TryRead(new FakeClipboard { Outcome = ClipboardTextOutcome.NoText }, Limits));
        Assert.Null(ClipboardTextFilter.TryRead(new FakeClipboard { Text = "x", Outcome = ClipboardTextOutcome.Busy }, Limits));
    }

    [Fact]
    public void ACopyTheAssistantMakesItselfIsSuppressedUntilItsStretchEnds()
    {
        using (ClipboardSuppression.Begin())
        {
            Assert.True(ClipboardSuppression.IsSuppressed);
            using (ClipboardSuppression.Begin())
            {
                Assert.True(ClipboardSuppression.IsSuppressed);
            }

            Assert.True(ClipboardSuppression.IsSuppressed);
        }

        // For a moment after, so that what is still on its way is not taken for the user's own copy.
        Assert.True(ClipboardSuppression.IsSuppressed);
    }

    [Fact]
    public void TheWatcherStartsAndStopsItsOwnWindowAndThreadAndCanBeStartedAgain()
    {
        using var watcher = new ClipboardWatcher(Limits);
        Assert.False(watcher.IsRunning);

        watcher.Start();
        watcher.Start();
        Assert.True(watcher.IsRunning);

        watcher.Stop();
        watcher.Stop();
        Assert.False(watcher.IsRunning);

        watcher.Start();
        Assert.True(watcher.IsRunning);
        watcher.Dispose();
        Assert.False(watcher.IsRunning);
        Assert.Throws<ObjectDisposedException>(watcher.Start);
    }

    [Fact]
    public void ManyWatchersCanRunAtOnceAndEachStopsOnItsOwn()
    {
        using var first = new ClipboardWatcher(Limits);
        using var second = new ClipboardWatcher(Limits);

        first.Start();
        second.Start();
        first.Stop();

        Assert.False(first.IsRunning);
        Assert.True(second.IsRunning);
    }

    // The real clipboard, only when asked for: the text on it is saved and put back, and what is copied is a made-up phrase.
    [Fact]
    public void TheRealClipboardIsWatchedAndASecretMarkedCopyIsNot()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_TEST_CLIPBOARD") != "1")
        {
            return;
        }

        var native = new ClipboardNativeMethods();
        var saved = native.TrySnapshot(out var snapshot);
        Assert.Equal(SnapshotFailure.None, saved);

        var seen = new List<string>();
        var arrived = new SemaphoreSlim(0);
        using var watcher = new ClipboardWatcher(ClipboardHistoryLimits.Default);
        watcher.TextCopied += (_, e) =>
        {
            lock (seen)
            {
                seen.Add(e.Text);
            }

            arrived.Release();
        };

        try
        {
            watcher.Start();

            Put("assistant clipboard test phrase 1", flag: null);
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "A copy was not noticed.");

            // A copy the application marks as not to be monitored is never passed on.
            Put("assistant clipboard SECRET phrase 2", flag: ClipboardTextFilter.ExcludeFromMonitoring);
            Assert.False(arrived.Wait(TimeSpan.FromSeconds(1.5)), "A copy marked private was passed on.");

            Put("assistant clipboard test phrase 3", flag: null);
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "A later copy was not noticed.");

            lock (seen)
            {
                Assert.Equal(["assistant clipboard test phrase 1", "assistant clipboard test phrase 3"], seen);
            }
        }
        finally
        {
            watcher.Stop();
            if (snapshot is not null)
            {
                native.Restore(snapshot);
            }
        }
    }

    private static void Put(string text, string? flag)
    {
        var owner = User32.CreateWindow(0, "STATIC", null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, Kernel32.GetModuleHandle(null), 0);
        try
        {
            for (var attempt = 0; attempt < 50 && !User32.OpenClipboard(owner); attempt++)
            {
                Thread.Sleep(20);
            }

            try
            {
                User32.EmptyClipboard();
                var bytes = (text.Length + 1) * 2;
                var memory = Kernel32.GlobalAlloc(0x0002, (nuint)bytes);
                var target = Kernel32.GlobalLock(memory);
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
                Kernel32.GlobalUnlock(memory);
                User32.SetClipboardData(User32.CF_UNICODETEXT, memory);

                if (flag is not null)
                {
                    var flagMemory = Kernel32.GlobalAlloc(0x0002, 4);
                    var flagTarget = Kernel32.GlobalLock(flagMemory);
                    Marshal.WriteInt32(flagTarget, 1);
                    Kernel32.GlobalUnlock(flagMemory);
                    User32.SetClipboardData(User32.RegisterClipboardFormat(flag), flagMemory);
                }
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
        finally
        {
            User32.DestroyWindow(owner);
        }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public ClipboardTextOutcome Outcome { get; set; } = ClipboardTextOutcome.Text;

        public HashSet<string> Formats { get; } = [];

        public Dictionary<string, int> Flags { get; } = [];

        public int AskedFor { get; private set; }

        public int TextReads { get; private set; }

        public bool HasRegisteredFormat(string name) => Formats.Contains(name);

        public int? ReadFlag(string name) => Flags.TryGetValue(name, out var value) ? value : null;

        public ClipboardTextOutcome ReadText(int maxLength, out string? text)
        {
            TextReads++;
            AskedFor = maxLength;
            text = Text is null ? null : Text[..Math.Min(Text.Length, maxLength)];
            return Outcome;
        }
    }
}
