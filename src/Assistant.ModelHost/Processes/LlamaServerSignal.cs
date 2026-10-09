namespace Assistant.ModelHost.Processes;

/// <summary>What a line of the llama.cpp server's output told the host (<see cref="LlamaServerOutput"/>).</summary>
internal enum LlamaServerSignalKind
{
    /// <summary>The server has loaded its model and listens: it is ready.</summary>
    Listening,

    /// <summary>The server has loaded its model.</summary>
    ModelLoaded,

    /// <summary>The server could not load its model, and exits.</summary>
    ModelLoadFailed,

    /// <summary>The server could not open its socket, and exits.</summary>
    EndpointFailed,

    /// <summary>The server could not allocate memory.</summary>
    OutOfMemory,

    /// <summary>The server's thread count, in <see cref="LlamaServerSignal.Count"/>.</summary>
    Threads,

    /// <summary>
    /// The server's parallel slots, in <see cref="LlamaServerSignal.Count"/>, each of
    /// <see cref="LlamaServerSignal.ContextTokens"/> tokens.
    /// </summary>
    Slots,

    /// <summary>A prompt of <see cref="LlamaServerSignal.Count"/> tokens was evaluated.</summary>
    PromptEvaluated,

    /// <summary><see cref="LlamaServerSignal.Count"/> tokens were generated.</summary>
    Generated,

    /// <summary>Some other warning. Its text is not kept.</summary>
    Warning,

    /// <summary>Some other error. Its text is not kept.</summary>
    Error,
}

/// <summary>
/// One understood line of the llama.cpp server's output, reduced to a kind and numbers. It holds no text, so nothing the
/// server printed, such as a model's path, can reach a log through it.
/// </summary>
internal readonly record struct LlamaServerSignal(LlamaServerSignalKind Kind)
{
    /// <summary>Threads, slots or tokens, as <see cref="Kind"/> says.</summary>
    public int Count { get; init; }

    /// <summary>The context of each slot, in tokens, for <see cref="LlamaServerSignalKind.Slots"/>.</summary>
    public int ContextTokens { get; init; }

    /// <summary>How long the work took, for timings.</summary>
    public double ElapsedMs { get; init; }
}
