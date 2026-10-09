using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Time;

/// <summary>
/// <c>get_time</c> (PROJECT_SPEC §4.8): the date and time now, on the user's own PC and in its time zone. A model does not know what time it is, and
/// said so when asked; with this it reads the PC's clock. It reads only, so nobody is asked, and it is offered in every request: what time it is can
/// be asked in too many ways to listen for, and the tool costs a request a line.
/// </summary>
public static class TimeTools
{
    /// <summary>The name of the tool that tells the date and time.</summary>
    public const string GetTimeName = "get_time";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><c>get_time</c>: the time, the date, the day of the week and the time zone, as <paramref name="clock"/> has them.</summary>
    public static ITool GetTime(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new HandlerTool(
            ToolDefinition.Create(
                GetTimeName,
                "Tell the date and time now on the user's PC, with the day of the week and the time zone. Use it whenever the user asks what time, what day or what date " +
                "it is, or anything that depends on them, such as how long until tonight or which day tomorrow is. Never say you cannot know the time: call this.",
                [],
                RiskLevel.ReadOnly,
                timeout: TimeSpan.FromSeconds(5)),
            (call, _, _, _) => Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, Describe(clock.GetLocalNow(), clock.LocalTimeZone))));
    }

    /// <summary>
    /// The JSON the model reads: the time and the date by fixed rules (24 hours, year first), and the same as the user's own PC writes them, to say.
    /// </summary>
    public static string Describe(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var offset = now.Offset;
        var utc = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        var name = zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("time", now.ToString("HH:mm", CultureInfo.InvariantCulture));
            writer.WriteString("date", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("day", now.ToString("dddd", CultureInfo.InvariantCulture));
            writer.WriteString("time_zone", string.IsNullOrWhiteSpace(name) ? utc : name + " (" + utc + ")");
            writer.WriteString("as_the_user_writes_it", now.ToString("t", CultureInfo.CurrentCulture) + ", " + now.ToString("D", CultureInfo.CurrentCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
