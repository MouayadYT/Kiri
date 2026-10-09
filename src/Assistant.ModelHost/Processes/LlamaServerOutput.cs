using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.ModelHost.Processes;

/// <summary>
/// Reads the llama.cpp server's log lines by an allow-list: the few operational lines the host understands become a
/// <see cref="LlamaServerSignal"/> of numbers, other warnings and errors become a bare count, and every other line is
/// dropped. No text from the server is kept or logged, because its lines carry model paths and, at higher verbosity,
/// prompts.
/// </summary>
/// <remarks>
/// The patterns match the bundled build (third_party/llama.cpp). A line reads
/// <c>0.00.040.792 I srv  llama_server: model loaded</c>: the time since start, a level letter (D, I, W or E), then the
/// message.
/// </remarks>
internal static partial class LlamaServerOutput
{
    // Longer lines are not the server's operational messages; they are dropped unread.
    private const int MaxLineLength = 2048;

    /// <summary>What <paramref name="line"/> says, or <see langword="null"/> for a line the host ignores.</summary>
    public static LlamaServerSignal? Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength)
        {
            return null;
        }

        var prefix = Prefix().Match(line);
        var level = prefix.Success ? prefix.Groups["level"].ValueSpan[0] : 'I';
        var message = prefix.Success ? prefix.Groups["message"].Value : line;

        if (Listening().IsMatch(message))
        {
            return new(LlamaServerSignalKind.Listening);
        }

        if (ModelLoaded().IsMatch(message))
        {
            return new(LlamaServerSignalKind.ModelLoaded);
        }

        if (ModelLoadFailed().IsMatch(message))
        {
            return new(LlamaServerSignalKind.ModelLoadFailed);
        }

        if (EndpointFailed().IsMatch(message))
        {
            return new(LlamaServerSignalKind.EndpointFailed);
        }

        if (OutOfMemory().IsMatch(message))
        {
            return new(LlamaServerSignalKind.OutOfMemory);
        }

        if (Threads().Match(message) is { Success: true } threads && TryCount(threads, "threads", out var threadCount))
        {
            return new(LlamaServerSignalKind.Threads) { Count = threadCount };
        }

        if (Slots().Match(message) is { Success: true } slots
            && TryCount(slots, "slots", out var slotCount)
            && TryCount(slots, "context", out var contextTokens))
        {
            return new(LlamaServerSignalKind.Slots) { Count = slotCount, ContextTokens = contextTokens };
        }

        if (Timing().Match(message) is { Success: true } timing
            && TryCount(timing, "tokens", out var tokens)
            && double.TryParse(timing.Groups["ms"].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            var kind = timing.Groups["prompt"].Success ? LlamaServerSignalKind.PromptEvaluated : LlamaServerSignalKind.Generated;
            return new(kind) { Count = tokens, ElapsedMs = ms };
        }

        return level switch
        {
            'W' => new(LlamaServerSignalKind.Warning),
            'E' => new(LlamaServerSignalKind.Error),
            _ => null,
        };
    }

    private static bool TryCount(Match match, string group, out int count) =>
        int.TryParse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out count);

    [GeneratedRegex(@"^\s*(?:\d+(?:\.\d+){3}\s+)?(?<level>[DIWE])\s(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Prefix();

    // "llama_server: listening on unix://C:\...\engine-1a2b3c4d.sock"
    [GeneratedRegex(@"\blistening on (?:unix|https?)://", RegexOptions.CultureInvariant)]
    private static partial Regex Listening();

    // "llama_server: model loaded"
    [GeneratedRegex(@":\s+model loaded\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelLoaded();

    // "llama_server: exiting due to model loading error"
    [GeneratedRegex(@"\bexiting due to model loading error\b", RegexOptions.CultureInvariant)]
    private static partial Regex ModelLoadFailed();

    // "start: couldn't bind HTTP server socket, ..." and "llama_server: exiting due to HTTP server error"
    [GeneratedRegex(@"\bcouldn't bind HTTP server socket\b|\bexiting due to HTTP server error\b", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointFailed();

    // ggml's "failed to allocate ... buffer" and the like.
    [GeneratedRegex(@"\bfailed to allocate\b|\bout of memory\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OutOfMemory();

    // "init: llama threadpool init, n_threads = 14"
    [GeneratedRegex(@"\bthreadpool init, n_threads = (?<threads>\d{1,6})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Threads();

    // "load_model: initializing, n_slots = 4, n_ctx_slot = 2048, kv_unified = 'true'"
    [GeneratedRegex(@"\bload_model: initializing, n_slots = (?<slots>\d{1,6}), n_ctx_slot = (?<context>\d{1,9})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Slots();

    // "print_timing: id  3 | task 0 | prompt eval time =       1.11 ms /    22 tokens (...)", and the same without
    // "prompt " for generation.
    [GeneratedRegex(@"\bprint_timing:.*\|\s*(?<prompt>prompt )?eval time =\s*(?<ms>\d{1,12}(?:\.\d+)?) ms /\s*(?<tokens>\d{1,9}) tokens\b", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();
}
