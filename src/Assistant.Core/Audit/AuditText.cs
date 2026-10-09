using System.Text;
using Assistant.Core.Confirmation;
using Assistant.Core.Tools;

namespace Assistant.Core.Audit;

/// <summary>
/// The words of the activity log (PROJECT_SPEC §3.2, §4.8, step 117). Everything the log keeps about a step is fixed here, in code, or is a name that
/// <see cref="Label"/> tidied: what a tool was called with and what it returned never reach the log, and neither does a message that a tool or a server
/// wrote about its own failure, since a tool's wording may repeat what it was given. A step is described by what the tool does, by a code that says why it
/// did not work, and by what the user answered.
/// </summary>
public static class AuditText
{
    /// <summary>The longest a name kept in the log is.</summary>
    public const int MaxLabelLength = 60;

    /// <summary>The longest a summary kept in the log is.</summary>
    public const int MaxSummaryLength = 120;

    /// <summary>The longest tool name kept in the log; the tool registry never gives a longer one.</summary>
    public const int MaxToolNameLength = 64;

    // A run of this many characters that look like a key or a token (letters and digits with a digit or one of + / = among them: a long word or a name joined by hyphens is
    // not one) is never kept, whatever it is in.
    private const int SecretRunLength = 24;

    private static readonly Dictionary<string, string> ToolPhrases = new(StringComparer.Ordinal)
    {
        ["open_application"] = "Open an application",
        ["open_file"] = "Open a file",
        ["reveal_file"] = "Show a file in its folder",
        ["open_folder"] = "Open a folder",
        ["get_volume"] = "Read the volume",
        ["set_volume"] = "Set the volume",
        ["mute"] = "Mute the sound",
        ["unmute"] = "Unmute the sound",
        ["take_screenshot"] = "Take a screenshot",
        ["get_time"] = "Read the date and time",
        ["set_do_not_disturb"] = "Switch Do not disturb",
        ["remember"] = "Remember something you said",
        ["set_alarm"] = "Set an alarm",
        ["start_timer"] = "Start a timer",
        ["control_timer"] = "Stop or pause a timer",
        ["stopwatch"] = "Use the stopwatch",
        ["start_focus_session"] = "Start a focus session",
        ["set_clock_display"] = "Choose the Clock window's display",
        ["control_home_device"] = "Control a home device",
        ["get_home_devices"] = "Look at your home devices",
        ["calculate"] = "Calculate",
        ["get_calendar_events"] = "Read calendar events",
        ["search_calendar_events"] = "Search calendar events",
        ["draft_message"] = "Draft a message",
        ["send_message"] = "Send a message",
        ["remember_person"] = "Remember who someone is",
        ["search_web"] = "Search the web",
        ["search_files"] = "Search your files",
        ["read_file_text"] = "Read a file",
        ["read_screen_text"] = "Read the text on the screen",
    };

    // What a code says, in the words that follow "didn't work:" or "not run:". A code that is not here is shown as the general one.
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        [ToolErrors.UnknownTool] = "that tool isn't available",
        [ToolErrors.InvalidArguments] = "the tool was asked in a way it can't take",
        [ToolErrors.PermissionOff] = "the permission it needs is off",
        [ToolErrors.NotAllowed] = "that is never allowed",
        [ToolErrors.Declined] = "you didn't allow it",
        [ToolErrors.NoAnswer] = "there was no answer in time",
        [ToolErrors.CouldNotAsk] = "you couldn't be asked",
        [ToolErrors.TimedOut] = "it took too long",
        [ToolErrors.Failed] = "the tool failed",
        [ToolErrors.Repeated] = "the same call had been made already",
        [ToolErrors.TooManyCalls] = "the run had no room for more calls",
        [ToolErrors.ResultTooLarge] = "the result was too large",

        // An integration that was looked for, offered, installed, updated or reconnected (codes of the integration actions).
        ["local_only"] = "Local Only mode is on",
        ["offer_expired"] = "the offer had expired",
        ["web_locked"] = "the web is turned off for the Assistant",
        ["already_installed"] = "it is installed already",
        ["busy"] = "another installation was running",
        ["download_failed"] = "the download failed",
        ["hash_mismatch"] = "the download was not what was reviewed",
        ["runtime_unavailable"] = "the program it needs could not be set up",
        ["setup_failed"] = "it could not be unpacked or set up",
        ["entry_point_missing"] = "it has no program to start",
        ["did_not_start"] = "it did not start",
        ["lacks_capability"] = "it cannot do what was asked",
        ["disk_failed"] = "the disk could not be written",
        ["unsupported"] = "that kind of package can't be set up",
        ["sign_in_failed"] = "the sign-in was not finished or was refused",
        ["app_not_running"] = "the app is not running on this PC, or its server is off",
        ["lookup_failed"] = "the places that list integrations could not be reached",
        ["not_updatable"] = "it can only be updated where it was set up",
        ["review_failed"] = "it did not pass the checks",
        ["not_connected"] = "it could not be reached",
        ["turned_off"] = "it is turned off",
    };

    /// <summary>
    /// A name made fit to keep: control characters and the invisible ones that reorder text taken out, spaces tidied, a run that looks like a key or a token
    /// replaced, and no more than <see cref="MaxLabelLength"/> characters. Empty text stays empty.
    /// </summary>
    public static string Label(string? text) => Tidy(text, MaxLabelLength);

    /// <summary>A summary made fit to keep, as <see cref="Label"/> makes a name, up to <see cref="MaxSummaryLength"/> characters.</summary>
    public static string Sentence(string? text) => Tidy(text, MaxSummaryLength);

    private static string Tidy(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength * 2));
        var run = 0;
        var runLooksSecret = false;
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                run = 0;
                runLooksSecret = false;
                continue;
            }

            if (char.IsControl(character) || IsInvisibleFormat(character))
            {
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            run = IsKeyCharacter(character) ? run + 1 : 0;
            runLooksSecret = run > 0 && (runLooksSecret || char.IsAsciiDigit(character) || character is '+' or '/' or '=');
            builder.Append(character);
            if (run >= SecretRunLength && runLooksSecret)
            {
                // The run is cut out whole: what is left is the part before it and a mark that something was left out.
                builder.Length -= run;
                builder.Append('…');
                run = 0;
                runLooksSecret = false;
            }
        }

        var tidy = builder.ToString().Trim();
        return tidy.Length <= maxLength ? tidy : tidy[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// A tool's name as the log keeps it: the name itself when it is one the tool registry could have given (lower-case words joined by underscores, at most
    /// <see cref="MaxToolNameLength"/> characters), otherwise a general name, so no text the model made up is ever kept as a name.
    /// </summary>
    public static string ToolName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxToolNameLength || !char.IsAsciiLetterLower(name[0]))
        {
            return "unknown_tool";
        }

        return name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_') ? name : "unknown_tool";
    }

    /// <summary>What a tool does, in a few words: "Send a message". A connected app's tool is "Use a connected app" and the app's name.</summary>
    public static string ToolPhrase(string? toolName)
    {
        var name = ToolName(toolName);
        if (ToolPhrases.TryGetValue(name, out var phrase))
        {
            return phrase;
        }

        if (ConnectedAppTools.IsConnectedAppTool(name) && ConnectedAppOf(name) is { Length: > 0 } app)
        {
            return $"Use a connected app ({app})";
        }

        return ConnectedAppTools.IsConnectedAppTool(name) ? "Use a connected app" : "Use a tool";
    }

    /// <summary>Whether <paramref name="toolName"/> is one of the Assistant's own tools that the log has words for.</summary>
    public static bool HasPhrase(string? toolName) => toolName is not null && ToolPhrases.ContainsKey(toolName);

    /// <summary>The names of the tools the log has words for.</summary>
    public static IReadOnlyCollection<string> KnownTools => ToolPhrases.Keys;

    /// <summary>
    /// The code to keep for a failure: <paramref name="code"/> when it is one the log has words for, <see cref="ToolErrors.Failed"/> for any other (a connected
    /// app may call a failure what it likes, and what it likes is not kept), and <see langword="null"/> when there is none.
    /// </summary>
    public static string? Code(string? code) =>
        string.IsNullOrEmpty(code) ? null : Reasons.ContainsKey(code) ? code : ToolErrors.Failed;

    /// <summary>Whether the log has words for <paramref name="code"/>.</summary>
    public static bool HasReason(string? code) => code is not null && Reasons.ContainsKey(code);

    /// <summary>Why a step did not work, in a few words that follow "didn't work:"; the general words for a code the log does not know.</summary>
    public static string Reason(string? code) =>
        code is not null && Reasons.TryGetValue(code, out var reason) ? reason : "something went wrong";

    /// <summary>How a step stands, for the person reading the log: "Done", "Waiting for you", "Didn't work: it took too long".</summary>
    public static string StatusText(AuditStatus status, ConfirmationDecision? confirmation = null, string? errorCode = null) => status switch
    {
        AuditStatus.Running => "Working",
        AuditStatus.WaitingForYou => "Waiting for you",
        AuditStatus.Succeeded => "Done",
        AuditStatus.Failed => errorCode is null ? "Didn't work" : "Didn't work: " + Reason(errorCode),
        AuditStatus.TimedOut => "Took too long, so it was given up",
        AuditStatus.Cancelled => "Stopped",
        AuditStatus.Skipped => errorCode is null ? "Not run" : "Not run: " + Reason(errorCode),
        AuditStatus.Interrupted => "Interrupted when the app closed",
        AuditStatus.Declined => confirmation switch
        {
            ConfirmationDecision.Declined => "You didn't allow it, so it wasn't done",
            ConfirmationDecision.NoAnswer => "No answer in time, so it wasn't done",
            ConfirmationDecision.CouldNotAsk => "You couldn't be asked, so it wasn't done",
            _ => "Not done",
        },
        _ => string.Empty,
    };

    /// <summary>What the user answered, in words; <see langword="null"/> when they were not asked.</summary>
    public static string? ConfirmationText(ConfirmationDecision? confirmation) => confirmation switch
    {
        ConfirmationDecision.Approved => "You allowed it",
        ConfirmationDecision.Declined => "You chose Don't allow",
        ConfirmationDecision.NoAnswer => "No answer in time",
        ConfirmationDecision.CouldNotAsk => "You couldn't be asked",
        _ => null,
    };

    /// <summary>The words for how a run stands, or how it ended.</summary>
    public static string TaskStatusText(AgentTaskStatus status) => status switch
    {
        AgentTaskStatus.Running => "Working on it",
        AgentTaskStatus.Completed => "Done",
        AgentTaskStatus.Incomplete => "Stopped early",
        AgentTaskStatus.Cancelled => "Stopped",
        AgentTaskStatus.Failed => "Couldn't finish",
        AgentTaskStatus.Interrupted => "Interrupted when the app closed",
        _ => string.Empty,
    };

    /// <summary>A connected app's name in the name of its tool (<c>mcp_samplecalendar_list_events</c> is <c>samplecalendar</c>), or empty.</summary>
    private static string ConnectedAppOf(string toolName)
    {
        var rest = toolName[ConnectedAppTools.Prefix.Length..];
        var end = rest.IndexOf('_', StringComparison.Ordinal);
        return end > 0 ? rest[..end] : rest;
    }

    private static bool IsKeyCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '/' or '=';

    // The characters that are invisible and change how text reads: zero-width ones, the marks that set a direction, the line and paragraph separators.
    private static bool IsInvisibleFormat(char character) =>
        character is (char)0x200B or (char)0x200C or (char)0x200D or (char)0x200E or (char)0x200F
            or (>= (char)0x202A and <= (char)0x202E) or (>= (char)0x2060 and <= (char)0x2064)
            or (char)0xFEFF or (char)0x2028 or (char)0x2029;
}
