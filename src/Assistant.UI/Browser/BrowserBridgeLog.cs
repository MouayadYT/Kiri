using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Browser;

/// <summary>The browser bridge's log messages: counts and outcomes, never selected text, a page's title or its address (PROJECT_SPEC §3.3).</summary>
internal static partial class BrowserBridgeLog
{
    [LoggerMessage(EventId = 9020, Level = LogLevel.Information, Message = "A browser sent a selection of {Length} characters (cut: {IsTruncated}, title: {HasTitle}, address: {HasAddress}, page text around it: {NearbyLength} characters)")]
    public static partial void SelectionReceived(ILogger logger, int length, bool isTruncated, bool hasTitle, bool hasAddress, int nearbyLength);

    [LoggerMessage(EventId = 9023, Level = LogLevel.Information, Message = "A browser selection was shown in the Ask panel (page text around it: {HasNearbyContext})")]
    public static partial void SelectionOpened(ILogger logger, bool hasNearbyContext);

    [LoggerMessage(EventId = 9024, Level = LogLevel.Information, Message = "A browser selection was not used: {Reason}")]
    public static partial void SelectionNotAllowed(ILogger logger, PermissionDecisionReason reason);

    [LoggerMessage(EventId = 9025, Level = LogLevel.Warning, Message = "A browser selection could not be shown: {ErrorType}")]
    public static partial void SelectionFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 9026, Level = LogLevel.Information, Message = "The window in front of a browser selection was noted: {Found}")]
    public static partial void BrowserWindowNoted(ILogger logger, bool found);

    [LoggerMessage(EventId = 9027, Level = LogLevel.Warning, Message = "The window in front of a browser selection could not be noted: {ErrorType}")]
    public static partial void BrowserWindowFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Information, Message = "Browser native-messaging host registration refreshed: {Succeeded}")]
    public static partial void RegistrationRefreshed(ILogger logger, bool succeeded);

    [LoggerMessage(EventId = 9022, Level = LogLevel.Warning, Message = "Browser native-messaging host registration could not be refreshed")]
    public static partial void RegistrationRefreshFailed(ILogger logger, Exception exception);
}
