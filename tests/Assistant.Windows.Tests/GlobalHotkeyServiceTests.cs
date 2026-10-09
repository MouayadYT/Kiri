using Assistant.Core.Settings;
using Assistant.Windows.Hotkeys;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Windows.Tests;

public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void DefaultIsAltAWithNoRepeatAndMatchingMessagesOnly()
    {
        var native = new FakeNative();
        using var service = new GlobalHotkeyService(new TestLogger(), native);
        Assert.True(service.Register(42, new HotkeySettings().SearchOrAsk));
        Assert.Equal((42, GlobalHotkeyService.HotkeyId, 0x4001u, 0x41u), native.LastRegistration);
        var invocations = 0;
        service.Invoked += (_, _) => invocations++;
        Assert.False(service.ProcessWindowMessage(43, 0x312, GlobalHotkeyService.HotkeyId, MessageData(1, 0x41)));
        Assert.False(service.ProcessWindowMessage(42, 0x312, 123, MessageData(1, 0x41)));
        Assert.False(service.ProcessWindowMessage(42, 0x312, GlobalHotkeyService.HotkeyId, MessageData(2, 0x41)));
        Assert.False(service.ProcessWindowMessage(42, 0x100, GlobalHotkeyService.HotkeyId, MessageData(1, 0x41)));
        Assert.True(service.ProcessWindowMessage(42, 0x312, GlobalHotkeyService.HotkeyId, MessageData(1, 0x41)));
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void ConflictIsNonFatalLoggedAndCanBeRetried()
    {
        var native = new FakeNative { RegisterError = 1409 };
        var logger = new TestLogger();
        using var service = new GlobalHotkeyService(logger, native);
        Assert.False(service.Register(42, new HotkeySettings().SearchOrAsk));
        Assert.False(service.IsRegistered);
        Assert.Contains(logger.Messages, line => line.Contains("1409"));
        native.RegisterError = 0;
        Assert.True(service.Register(42, new HotkeySettings().SearchOrAsk));
        Assert.True(service.IsRegistered);
    }

    [Fact]
    public void ReconfigureReleasesOldShortcutAndRejectsQueuedOldMessages()
    {
        var native = new FakeNative();
        using var service = new GlobalHotkeyService(new TestLogger(), native);
        var shortcut = new HotkeySettings().SearchOrAsk;
        Assert.True(service.Register(42, shortcut));
        Assert.True(service.Register(42, shortcut));
        Assert.Equal(1, native.RegisterCalls);
        Assert.True(service.Register(42, new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Space")));
        Assert.Equal(1, native.UnregisterCalls);
        Assert.Equal((42, GlobalHotkeyService.HotkeyId, 0x4006u, 0x20u), native.LastRegistration);
        Assert.False(service.ProcessWindowMessage(42, 0x312, GlobalHotkeyService.HotkeyId, MessageData(1, 0x41)));
        Assert.True(service.Register(42, null));
        Assert.False(service.IsRegistered);
        Assert.Equal(2, native.UnregisterCalls);
    }

    [Theory]
    [InlineData("", HotkeyModifiers.Alt)]
    [InlineData("private-sample-key-value", HotkeyModifiers.Alt)]
    [InlineData("A", (HotkeyModifiers)32)]
    public void InvalidConfigurationIsRejectedWithoutLoggingItsValue(string key, HotkeyModifiers modifiers)
    {
        var native = new FakeNative();
        var logger = new TestLogger();
        using var service = new GlobalHotkeyService(logger, native);
        Assert.False(service.Register(42, new Hotkey(modifiers, key)));
        Assert.Equal(0, native.RegisterCalls);
        Assert.Contains(logger.Messages, line => line.Contains("configuration is invalid"));
        Assert.DoesNotContain(logger.Messages, line => line.Contains("private-sample-key-value"));
    }

    [Theory]
    [InlineData("a", 0x41u)]
    [InlineData("7", 0x37u)]
    [InlineData("F24", 0x87u)]
    [InlineData("Space", 0x20u)]
    [InlineData("PageDown", 0x22u)]
    public void ConfiguredKeyNamesMapToVirtualKeys(string name, uint expected)
    {
        Assert.True(HotkeyKey.TryParse(name, out var key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void UnregisterFailureIsLoggedAndBlocksReplacementUntilRetry()
    {
        var native = new FakeNative();
        var logger = new TestLogger();
        using var service = new GlobalHotkeyService(logger, native);
        service.Register(42, new HotkeySettings().SearchOrAsk);
        native.UnregisterError = 5;
        Assert.False(service.Register(42, new Hotkey(HotkeyModifiers.Control, "B")));
        Assert.Equal(1, native.RegisterCalls);
        Assert.True(service.IsRegistered);
        Assert.Contains(logger.Messages, line => line.Contains("could not be unregistered"));
        native.UnregisterError = 0;
        Assert.True(service.Unregister());
        Assert.False(service.IsRegistered);
    }

    [Fact]
    public void DestroyAndDisposeReleaseRegistrationOnce()
    {
        var native = new FakeNative();
        var service = new GlobalHotkeyService(new TestLogger(), native);
        service.Register(42, new HotkeySettings().SearchOrAsk);
        Assert.False(service.ProcessWindowMessage(42, 0x2, 0, 0));
        Assert.False(service.IsRegistered);
        service.Dispose();
        service.Dispose();
        Assert.Equal(1, native.UnregisterCalls);
        Assert.False(service.ProcessWindowMessage(42, 0x312, GlobalHotkeyService.HotkeyId, MessageData(1, 0x41)));
        Assert.Throws<ObjectDisposedException>(() => service.Register(42, new HotkeySettings().SearchOrAsk));
    }

    [Fact]
    public void DisposeReleasesLiveRegistration()
    {
        var native = new FakeNative();
        var service = new GlobalHotkeyService(new TestLogger(), native);
        service.Register(42, new HotkeySettings().SearchOrAsk);
        service.Dispose();
        Assert.Equal(1, native.UnregisterCalls);
        Assert.False(service.IsRegistered);
    }

    private static nint MessageData(uint modifiers, uint key) => (nint)((key << 16) | modifiers);

    private sealed class FakeNative : IHotkeyNativeMethods
    {
        public int RegisterError { get; set; }
        public int UnregisterError { get; set; }
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public (nint, int, uint, uint) LastRegistration { get; private set; }
        public int Register(nint window, int id, uint modifiers, uint key)
        {
            RegisterCalls++;
            LastRegistration = (window, id, modifiers, key);
            return RegisterError;
        }
        public int Unregister(nint window, int id) { UnregisterCalls++; return UnregisterError; }
    }

    private sealed class TestLogger : ILogger<GlobalHotkeyService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
