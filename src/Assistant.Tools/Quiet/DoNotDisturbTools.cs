using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Tools;

namespace Assistant.Tools.Quiet;

/// <summary>
/// <c>set_do_not_disturb</c> (PROJECT_SPEC §4.8): turns Windows' Do not disturb on or off, the same switch as the bell in the notification centre.
/// It is one of the small things the user asks for in words and can switch back at once, so nobody is asked first
/// (<see cref="ToolDefinition.RunsWithoutAsking"/>): the step is shown in the conversation and kept in the activity log. It goes through
/// <see cref="ISystemActions"/>, the same small closed list as the volume, and what it returns is what Windows shows afterwards, read back.
/// </summary>
public static class DoNotDisturbTools
{
    /// <summary>The name of the tool that switches Do not disturb.</summary>
    public const string SetName = "set_do_not_disturb";

    // Offered only for a request about notifications or quiet: it runs without a question.
    private static readonly RequestTopic Quiet = new(
        "disturb", "dnd", "notification", "notifications", "interruption", "interruptions", "interrupt", "quiet", "silence", "silent", "focus",
        "distraction", "distractions");

    /// <summary><c>set_do_not_disturb</c>, over <paramref name="system"/>.</summary>
    public static ITool Set(ISystemActions system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return new HandlerTool(
            ToolDefinition.Create(
                SetName,
                "Turn Windows' Do not disturb on or off: while it is on, notifications are held back quietly. Use it when the user asks to turn Do not disturb, " +
                "notifications or interruptions on or off. It is done at once; the user is not asked.",
                [new ToolParameter("on", ToolParameterType.Boolean, "true to turn Do not disturb on (no notifications), false to turn it off.")],
                RiskLevel.SideEffect,
                timeout: TimeSpan.FromSeconds(10),
                runsWithoutAsking: true),
            (call, arguments, _, _) =>
            {
                if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("on", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Task.FromResult(ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, "Say whether to turn it on (true) or off (false)."));
                }

                var on = value.GetBoolean();
                return Task.FromResult(system.SetDoNotDisturb(on)
                    ? new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, SystemToolResults.Done(on ? "Do not disturb is on." : "Do not disturb is off."))
                    : ToolErrors.Result(
                        call, ToolResultStatus.Failed, ToolErrors.Failed,
                        "Windows did not switch Do not disturb. Nothing was changed. Tell the user they can switch it with the bell in the notification centre (Windows key + N)."));
            },
            isOffered: Quiet.IsAbout,
            focused: true);
    }
}
