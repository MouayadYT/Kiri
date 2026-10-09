using System.Text.Json;

namespace Assistant.ModelHost.FakeEngine;

/// <summary>
/// How the fake engine behaves. A test writes it to a file and hands that file to the engine as its model, as the model
/// host would hand the server a GGUF file: the file starts with the GGUF magic, which the host checks, and holds the
/// scenario as JSON after it. Launches are counted in a file beside it.
/// </summary>
public sealed record FakeEngineScenario
{
    /// <summary>How long it "loads the model" before it says it listens.</summary>
    public int ReadyDelayMs { get; init; } = 20;

    /// <summary>Whether it never says it listens, as a server stuck loading.</summary>
    public bool NeverReady { get; init; }

    /// <summary>
    /// <c>"model"</c> to fail loading the model, or <c>"bind"</c> to fail opening the socket, the way the real server
    /// reports each before it exits with code 1; <see langword="null"/> to start.
    /// </summary>
    public string? StartupFailure { get; init; }

    /// <summary>The first launch that has the <see cref="StartupFailure"/>; earlier ones start.</summary>
    public int StartupFailureFromLaunch { get; init; } = 1;

    /// <summary>Whether the <see cref="StartupFailure"/> happens only when the launch names devices to offload to, as a driver that cannot run the model does.</summary>
    public bool StartupFailureOnDevicesOnly { get; init; }

    /// <summary>An exit code to exit with at once and silently, as Windows does when a DLL is missing.</summary>
    public int? StartupExitCode { get; init; }

    /// <summary>How many launches exit on their own after they are ready; -1 for every launch.</summary>
    public int CrashingLaunches { get; init; }

    /// <summary>How long a crashing launch runs once it is ready.</summary>
    public int CrashAfterMs { get; init; } = 50;

    /// <summary>The exit code of a crashing launch.</summary>
    public int CrashExitCode { get; init; } = 3;

    /// <summary>How it answers chat completions once it listens.</summary>
    public FakeChatReply Chat { get; init; } = new();

    /// <summary>The first bytes of every GGUF file.</summary>
    public const string Magic = "GGUF";

    /// <summary>Writes the scenario to <paramref name="path"/>.</summary>
    public void Save(string path) => File.WriteAllText(path, Magic + JsonSerializer.Serialize(this));

    /// <summary>Reads the scenario at <paramref name="path"/>, or returns <see langword="null"/> when there is none.</summary>
    public static FakeEngineScenario? TryLoad(string? path)
    {
        try
        {
            if (path is null)
            {
                return null;
            }

            var text = File.ReadAllText(path);
            return text.StartsWith(Magic, StringComparison.Ordinal)
                ? JsonSerializer.Deserialize<FakeEngineScenario>(text[Magic.Length..])
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// How the fake engine answers <c>POST /v1/chat/completions</c>, in the real server's streamed format. Each request's
/// body is written beside the scenario (<see cref="FakeChatReply.RequestPath"/>) for the test to read, and a request
/// whose client went away before the answer ended leaves a file saying so (<see cref="FakeChatReply.DisconnectedPath"/>).
/// </summary>
public sealed record FakeChatReply
{
    /// <summary>The pieces of text it streams, one chunk each.</summary>
    public IReadOnlyList<string> Pieces { get; init; } = ["Hello", " there", "!"];

    /// <summary>How long it waits before each piece, as a model takes time for each token.</summary>
    public int PieceDelayMs { get; init; }

    /// <summary>How long it waits before its first chunk, as the model reads the prompt.</summary>
    public int FirstPieceDelayMs { get; init; }

    /// <summary>The finish reason of the last chunk: <c>stop</c>, <c>length</c> or <c>tool_calls</c>.</summary>
    public string FinishReason { get; init; } = "stop";

    /// <summary>Tool calls it streams after the text, each name with its arguments in two pieces.</summary>
    public IReadOnlyList<FakeToolCall> ToolCalls { get; init; } = [];

    /// <summary>
    /// <c>"context"</c> to refuse the prompt as longer than the context (HTTP 400), <c>"server"</c> to fail with HTTP
    /// 500, or <c>"stream"</c> to report an error in the stream after the pieces; <see langword="null"/> to answer.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Whether it exits in the middle of the answer, after the first piece, as an engine that crashes.</summary>
    public bool CrashMidAnswer { get; init; }

    /// <summary>Whether it closes the connection after the pieces without finishing the answer.</summary>
    public bool BreakOff { get; init; }

    /// <summary>The prompt and output token counts of the usage chunk.</summary>
    public int PromptTokens { get; init; } = 21;

    /// <summary>The file request <paramref name="number"/> (from 1) of the scenario at <paramref name="scenarioPath"/> was written to.</summary>
    public static string RequestPath(string scenarioPath, int number) => $"{scenarioPath}.chat-{number}.json";

    /// <summary>The file that says request <paramref name="number"/>'s client left before the answer ended.</summary>
    public static string DisconnectedPath(string scenarioPath, int number) => $"{scenarioPath}.chat-{number}.disconnected";
}

/// <summary>A tool call the fake engine streams.</summary>
public sealed record FakeToolCall(string Name, string Arguments);

/// <summary>What one launch of the fake engine saw, written beside its scenario for the test to read.</summary>
/// <param name="Launch">Which launch this was, from 1.</param>
/// <param name="ProcessId">Its process id.</param>
/// <param name="Arguments">Its command line.</param>
/// <param name="HasConsoleWindow">Whether it had a console window.</param>
/// <param name="LlamaVariables">The names of the LLAMA_*, HF_* and GGML_* variables in its environment.</param>
/// <param name="WorkingDirectory">Its working directory.</param>
public sealed record FakeEngineReport(
    int Launch,
    int ProcessId,
    IReadOnlyList<string> Arguments,
    bool HasConsoleWindow,
    IReadOnlyList<string> LlamaVariables,
    string WorkingDirectory)
{
    /// <summary>The file that counts the launches of the scenario at <paramref name="scenarioPath"/>.</summary>
    public static string CountPath(string scenarioPath) => scenarioPath + ".launches";

    /// <summary>The report of launch <paramref name="launch"/> of the scenario at <paramref name="scenarioPath"/>.</summary>
    public static string PathOf(string scenarioPath, int launch) => $"{scenarioPath}.launch-{launch}.json";

    /// <summary>Reads the report of launch <paramref name="launch"/>.</summary>
    public static FakeEngineReport Load(string scenarioPath, int launch) =>
        JsonSerializer.Deserialize<FakeEngineReport>(File.ReadAllText(PathOf(scenarioPath, launch)))!;

    /// <summary>Writes this report.</summary>
    public void Save(string scenarioPath) => File.WriteAllText(PathOf(scenarioPath, Launch), JsonSerializer.Serialize(this));
}
