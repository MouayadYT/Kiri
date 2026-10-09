using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Assistant.Core.Settings;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Hotkeys;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void NativeHotkeyReopensAndFocusesSameOverlayAndReleasesOnClose() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var window = CreateAssistant().Window;
        window.ShowActivated = false;
        // An uncommon chord keeps native verification independent of the user's configured Alt+A.
        var shortcut = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F23");
        using var hotkeys = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        using var binding = new OverlayHotkeyBinding(window, hotkeys, shortcut);
        using var competitorWindow = new HwndSource(new HwndSourceParameters("Hotkey registration test")
        {
            Width = 1, Height = 1, WindowStyle = 0,
        });
        using var competitor = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        try
        {
            window.Show();
            Assert.True(hotkeys.IsRegistered);
            Assert.False(competitor.Register(competitorWindow.Handle, shortcut));
            var input = Assert.IsType<PromptInputControl>(window.FindName("PromptInput"));
            input.Text = "synthetic hotkey draft";
            var handle = new WindowInteropHelper(window).Handle;
            var windowCount = app.Windows.Count;
            var invocations = 0;
            hotkeys.Invoked += (_, _) => invocations++;

            window.Hide();
            for (var i = 1; i <= 3; i++)
            {
                PressTestShortcut();
                WaitUntil(() => invocations == i);
                Assert.True(window.IsVisible);
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.Equal(handle, new WindowInteropHelper(window).Handle);
                Assert.Equal(windowCount, app.Windows.Count);
                Assert.Equal("synthetic hotkey draft", input.Text);
                // Also exercise refocusing an already-visible overlay on the second invocation.
                if (i == 2) window.Hide();
            }

            window.Close();
            Assert.False(hotkeys.IsRegistered);
            Assert.True(competitor.Register(competitorWindow.Handle, shortcut));
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void HotkeyMessageOpensTheBarAndBringsBackTheConversationItGrewInto() => RunSta(() =>
    {
        // The registration is real; the message Windows sends when the chord is pressed is posted by hand, so this
        // needs no input access. The invocation is the app's own: the controller's.
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var assistant = CreateAssistant();
        var window = assistant.Window;
        window.ShowActivated = false;
        var shortcut = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F22");
        using var hotkeys = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        using var binding = new OverlayHotkeyBinding(window, hotkeys, shortcut, assistant.Controller.Invoke);
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Assert.True(hotkeys.IsRegistered);
            var invocations = 0;
            hotkeys.Invoked += (_, _) => invocations++;
            void PressChord() => PostMessage(handle, 0x0312 /* WM_HOTKEY */, 0x5341, (0x85 << 16) | 0x7);

            PressChord();
            WaitUntil(() => invocations == 1 && window.IsVisible);
            Assert.Equal(AssistantWindowState.Compact, window.State);

            // Asked from the bar, it is the conversation; the chord then brings that same window back.
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "Synthetic question";
            Assert.True(input.TrySubmit());
            WaitUntil(() => !window.IsExpanding, "The bar did not finish growing.");
            PressChord();
            WaitUntil(() => invocations == 2);
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.True(window.IsVisible);
            Assert.Equal(1, ShownAssistants());

            // Once the conversation is gone, the chord opens the bar again, in the very same window.
            window.Hide();
            Assert.Equal(AssistantWindowState.Compact, window.State);
            PressChord();
            WaitUntil(() => invocations == 3 && window.IsVisible);
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.Equal(1, ShownAssistants());
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void ConflictAndDisabledShortcutLeaveOverlayUsable() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        using var ownerWindow = new HwndSource(new HwndSourceParameters("Hotkey conflict test")
        {
            Width = 1, Height = 1, WindowStyle = 0,
        });
        var shortcut = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F24");
        using var owner = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        Assert.True(owner.Register(ownerWindow.Handle, shortcut));
        var assistant = CreateAssistant();
        var window = assistant.Window;
        window.ShowActivated = false;
        using var hotkeys = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        try
        {
            using (var binding = new OverlayHotkeyBinding(window, hotkeys, shortcut))
            {
                window.ShowAndFocus();
                Assert.True(window.IsVisible);
                Assert.False(hotkeys.IsRegistered);
                var input = Assert.IsType<PromptInputControl>(window.FindName("PromptInput"));
                Assert.True(input.IsKeyboardFocusWithin);
                input.Text = "still editable";
                Assert.Equal("still editable", assistant.Bar.Query);
            }

            using var disabled = new OverlayHotkeyBinding(window, hotkeys, null);
            Assert.False(hotkeys.IsRegistered);
            window.Hide();
            window.ShowAndFocus();
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    private static void WaitUntil(Func<bool> condition, string failure = "Native hotkey did not reach the overlay.")
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), failure);
    }

    private static void PressTestShortcut()
    {
        // Real keyboard input verifies RegisterHotKey -> WM_HOTKEY -> ShowAndFocus, including modifiers.
        ushort[] keys = [0x11, 0x12, 0x10, 0x86]; // Ctrl, Alt, Shift, F23.
        var inputs = keys.Select(key => new KeyboardInput { Type = 1, VirtualKey = key })
            .Concat(keys.Reverse().Select(key => new KeyboardInput { Type = 1, VirtualKey = key, Flags = 2 }))
            .ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInput>());
        Assert.True(sent == inputs.Length,
            $"Windows input injection failed (Win32 error {Marshal.GetLastWin32Error()}); this integration test requires desktop input access.");
    }

    // INPUT is 40 bytes in this test project's x64 process; the union starts at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(12)] public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] inputs, int size);
}
