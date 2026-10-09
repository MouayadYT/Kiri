using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assistant.Core.Agent;

/// <summary>
/// Notices a run that is going in circles (PROJECT_SPEC §4.8, step 114): a model that makes the same call over and over, or whose rounds of calls
/// all come to nothing, gains nothing from more rounds. The same call twice is not run twice (the model is told to use its result); when it keeps
/// doing it (<see cref="AgentLimits.MaxRepeatedCalls"/>), or when rounds in a row do nothing (<see cref="AgentLimits.MaxRoundsWithoutProgress"/>:
/// every call failed, was refused or repeated one already made), the run is stopped and the model is asked for its final answer.
/// </summary>
internal sealed class AgentLoopGuard(AgentLimits limits)
{
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);
    private int _repeats;
    private int _idleRounds;

    /// <summary>Whether the guard has seen enough to say the run is going nowhere.</summary>
    public bool IsLooping => _repeats >= limits.MaxRepeatedCalls || _idleRounds >= limits.MaxRoundsWithoutProgress;

    /// <summary>
    /// Remembers a call that is about to be run, and says whether it is one that was already made in the run. The same tool with the same
    /// arguments, however the JSON is spaced, is the same call.
    /// </summary>
    public bool IsRepeat(string toolName, string argumentsJson)
    {
        if (_made.Add(Key(toolName, argumentsJson)))
        {
            return false;
        }

        _repeats++;
        return true;
    }

    /// <summary>Closes a round: it made progress when at least one call was run and worked.</summary>
    public void EndRound(bool madeProgress) => _idleRounds = madeProgress ? 0 : _idleRounds + 1;

    /// <summary>What makes two calls the same: the tool and its arguments, however the JSON is spaced.</summary>
    public static string Key(string toolName, string argumentsJson)
    {
        try
        {
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return toolName + "|" + arguments.RootElement.GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return toolName + "|" + argumentsJson;
        }
    }

    /// <summary>A short hash of a call's arguments: it tells two calls apart in a trace without saying what they were.</summary>
    public static string Fingerprint(string toolName, string argumentsJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Key(toolName, argumentsJson)));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}
