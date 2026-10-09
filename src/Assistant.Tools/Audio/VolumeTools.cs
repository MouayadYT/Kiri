using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Tools;

namespace Assistant.Tools.Audio;

/// <summary>
/// The tools for the default speakers' sound (PROJECT_SPEC §4.8): <c>get_volume</c> (read only), <c>set_volume</c>, which changes it and so is
/// confirmed by the user (who can choose Always allow), and <c>mute</c> and <c>unmute</c>, a switch the user asks for in words and can flip back at
/// once, so nobody is asked first (<see cref="ToolDefinition.RunsWithoutAsking"/>); the step is shown and kept in the activity log. They go through <see cref="ISystemActions"/>, the same small
/// closed list the bar's quick actions use, so the volume is moved by Windows' own Core Audio and nothing else is started. A volume is a
/// whole number from 0 to 100, and what is returned is what the speakers are now, read back.
/// </summary>
public static class VolumeTools
{
    private const string NoOutput = "There is no sound output on this PC, or its volume could not be read. Tell the user.";

    // The tools are offered only for a request about the sound (and the few minutes after it in the same conversation): mute and unmute run without a question.
    private static readonly RequestTopic Sound = new(
        "volume", "sound", "sounds", "mute", "muted", "unmute", "unmuted", "audio", "speaker", "speakers", "louder", "quieter", "loud", "quiet",
        "silence", "silent", "loudness");

    /// <summary><c>get_volume</c>: the volume and whether the sound is off.</summary>
    public static ITool GetVolume(ISystemActions system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return new HandlerTool(
            ToolDefinition.Create(
                SystemToolResults.GetVolume,
                "Read the speakers' volume, from 0 to 100, and whether the sound is off.",
                [],
                RiskLevel.ReadOnly,
                timeout: TimeSpan.FromSeconds(10)),
            (call, _, _, _) => Task.FromResult(system.GetVolume() is { } state
                ? Succeeded(call, SystemToolResults.Volume(state.Percent, state.Muted))
                : Failed(call, NoOutput)),
            isOffered: Sound.IsAbout,
            focused: true);
    }

    /// <summary><c>set_volume</c>: sets the volume, and turns the sound on when it is more than nothing, as the volume keys do.</summary>
    public static ITool SetVolume(ISystemActions system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return new HandlerTool(
            ToolDefinition.Create(
                SystemToolResults.SetVolume,
                "Set the speakers' volume to a number from 0 to 100. A volume above 0 also turns the sound on.",
                [new ToolParameter("percent", ToolParameterType.Integer, "The volume, a whole number from 0 to 100.", Minimum: 0, Maximum: 100)],
                RiskLevel.SideEffect,
                timeout: TimeSpan.FromSeconds(10)),
            (call, arguments, _, _) => Task.FromResult(
                Change(call, system, () => system.SetVolume(ReadPercent(arguments)), now => $"The volume is now {now}%.")),
            confirmation: arguments =>
            {
                var percent = ReadPercent(arguments);
                var now = system.GetVolume();
                return new ToolConfirmation(
                    ConfirmationKind.ChangeSystem,
                    $"Set the volume to {percent}%?",
                    [
                        new ConfirmationDetail("Change", $"Set the speakers' volume to {percent}%" + (percent > 0 ? ", with the sound on" : string.Empty)),
                        new ConfirmationDetail("Now", now is { } state ? $"{state.Percent}%" + (state.Muted ? ", sound off" : string.Empty) : "Not known"),
                    ],
                    "Set volume");
            },
            isOffered: Sound.IsAbout,
            focused: true);
    }

    /// <summary><c>mute</c>: turns the sound off.</summary>
    public static ITool Mute(ISystemActions system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return new HandlerTool(
            ToolDefinition.Create(
                SystemToolResults.Mute,
                "Turn the speakers' sound off. The volume is kept, so unmute brings it back.",
                [],
                RiskLevel.SideEffect,
                timeout: TimeSpan.FromSeconds(10),
                runsWithoutAsking: true),
            (call, _, _, _) => Task.FromResult(Change(call, system, () => system.SetMuted(true), _ => "The sound is off.")),
            confirmation: _ => new ToolConfirmation(
                ConfirmationKind.ChangeSystem,
                "Turn the sound off?",
                [new ConfirmationDetail("Change", "Mute the speakers. The volume is kept.")],
                "Mute"),
            isOffered: Sound.IsAbout,
            focused: true);
    }

    /// <summary><c>unmute</c>: turns the sound on.</summary>
    public static ITool Unmute(ISystemActions system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return new HandlerTool(
            ToolDefinition.Create(
                SystemToolResults.Unmute,
                "Turn the speakers' sound on again.",
                [],
                RiskLevel.SideEffect,
                timeout: TimeSpan.FromSeconds(10),
                runsWithoutAsking: true),
            (call, _, _, _) => Task.FromResult(Change(call, system, () => system.SetMuted(false), _ => "The sound is on.")),
            confirmation: _ => new ToolConfirmation(
                ConfirmationKind.ChangeSystem,
                "Turn the sound on?",
                [new ConfirmationDetail("Change", "Unmute the speakers.")],
                "Unmute"),
            isOffered: Sound.IsAbout,
            focused: true);
    }

    // Does the change, then reads the volume back, so that what the model says of it is what the speakers are.
    private static ToolResult Change(ToolCall call, ISystemActions system, Func<bool> change, Func<int, string> message)
    {
        if (!change() || system.GetVolume() is not { } state)
        {
            return Failed(call, NoOutput);
        }

        return Succeeded(call, SystemToolResults.Volume(state.Percent, state.Muted, message(state.Percent)));
    }

    private static int ReadPercent(JsonElement arguments) => arguments.GetProperty("percent").GetInt32();

    private static ToolResult Succeeded(ToolCall call, string output) => new(call.Id, call.ToolName, ToolResultStatus.Succeeded, output);

    private static ToolResult Failed(ToolCall call, string message) =>
        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);
}
