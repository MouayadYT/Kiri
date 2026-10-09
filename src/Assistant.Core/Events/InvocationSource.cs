using System.Text.Json.Serialization;

namespace Assistant.Core.Events;

/// <summary>How the user invoked the assistant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvocationSource>))]
public enum InvocationSource
{
    /// <summary>A global hotkey.</summary>
    Hotkey = 0,

    /// <summary>The tray icon's menu.</summary>
    TrayMenu = 1,

    /// <summary>The File Explorer entry point, over IPC.</summary>
    ExplorerExtension = 2,

    /// <summary>The browser extension, over IPC.</summary>
    BrowserBridge = 3,
}
