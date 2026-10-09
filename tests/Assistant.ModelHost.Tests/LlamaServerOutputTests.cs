using Assistant.ModelHost.Processes;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>Reading the llama.cpp server's output by an allow-list. The lines are the bundled build's own.</summary>
public sealed class LlamaServerOutputTests
{
    [Theory]
    [InlineData(@"0.00.040.800 I srv  llama_server: listening on unix://C:\Users\PRIVATE-PATH-0c5a\engine-1a2b3c4d.sock", nameof(LlamaServerSignalKind.Listening))]
    [InlineData("0.00.040.792 I srv  llama_server: model loaded", nameof(LlamaServerSignalKind.ModelLoaded))]
    [InlineData("0.00.036.869 E srv  llama_server: exiting due to model loading error", nameof(LlamaServerSignalKind.ModelLoadFailed))]
    [InlineData(@"0.00.022.310 E srv         start: couldn't bind HTTP server socket, hostname: C:\PRIVATE-PATH-0c5a\x.sock, port: 8080", nameof(LlamaServerSignalKind.EndpointFailed))]
    [InlineData("0.00.022.399 E srv  llama_server: exiting due to HTTP server error", nameof(LlamaServerSignalKind.EndpointFailed))]
    [InlineData("0.00.500.000 E ggml_backend_cpu_buffer_type_alloc_buffer: failed to allocate buffer of size 8589934592", nameof(LlamaServerSignalKind.OutOfMemory))]
    [InlineData("0.00.028.437 W load: bad special token: 'tokenizer.ggml.seperator_token_id' = 4294967295, using default id -1", nameof(LlamaServerSignalKind.Warning))]
    [InlineData(@"0.00.036.346 E llama_model_load: error loading model: llama_model_loader: failed to load model from C:\PRIVATE-PATH-0c5a\m.gguf", nameof(LlamaServerSignalKind.Error))]
    [InlineData("0.00.031.818 W srv  llama_server: security: no API key is set and CORS allows all origins (see https://github.com/ggml-org/llama.cpp/pull/25655)", nameof(LlamaServerSignalKind.Warning))]
    public void UnderstoodLines_BecomeSignals(string line, string expected)
    {
        var signal = LlamaServerOutput.Classify(line);

        Assert.Equal(Enum.Parse<LlamaServerSignalKind>(expected), signal?.Kind);
    }

    [Fact]
    public void Counts_AreRead()
    {
        Assert.Equal(
            new LlamaServerSignal(LlamaServerSignalKind.Threads) { Count = 14 },
            LlamaServerOutput.Classify("0.00.032.459 I cmn          init: llama threadpool init, n_threads = 14"));
        Assert.Equal(
            new LlamaServerSignal(LlamaServerSignalKind.Slots) { Count = 4, ContextTokens = 2048 },
            LlamaServerOutput.Classify("0.00.037.598 I srv    load_model: initializing, n_slots = 4, n_ctx_slot = 2048, kv_unified = 'true'"));
        Assert.Equal(
            new LlamaServerSignal(LlamaServerSignalKind.PromptEvaluated) { Count = 22, ElapsedMs = 1.11 },
            LlamaServerOutput.Classify("0.02.059.981 I slot print_timing: id  3 | task 0 | prompt eval time =       1.11 ms /    22 tokens (    0.05 ms per token, 19855.60 tokens per second)"));
        Assert.Equal(
            new LlamaServerSignal(LlamaServerSignalKind.Generated) { Count = 16, ElapsedMs = 5.08 },
            LlamaServerOutput.Classify("0.02.059.985 I slot print_timing: id  3 | task 0 |        eval time =       5.08 ms /    16 tokens (    0.34 ms per token,  2950.43 tokens per second)"));
    }

    [Theory]
    [InlineData(@"0.00.021.652 I srv    load_model: loading model 'C:\Users\PRIVATE-PATH-0c5a\model.gguf'")]
    [InlineData("0.00.001.492 I srv  llama_server: initializing ...")]
    [InlineData("0.02.059.985 I slot print_timing: id  3 | task 0 |       total time =       6.19 ms /    38 tokens")]
    [InlineData("0.02.060.002 I slot      release: id  3 | task 0 | stop processing: n_tokens = 37, truncated = 0")]
    [InlineData("PRIVATE-PROMPT-7f3c: summarize my tax letter")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void OtherLines_AreDropped(string? line)
    {
        Assert.Null(LlamaServerOutput.Classify(line));
    }

    [Fact]
    public void OverlongLine_IsDroppedUnread()
    {
        Assert.Null(LlamaServerOutput.Classify("0.00.000.001 E " + new string('x', 5000)));
    }

    [Fact]
    public void ALineThatOnlyMentionsListening_DoesNotMakeTheServerReady()
    {
        var signal = LlamaServerOutput.Classify("0.00.000.001 W srv  PRIVATE-PROMPT-7f3c said listening on the radio");

        Assert.Equal(LlamaServerSignalKind.Warning, signal?.Kind);
    }
}

/// <summary>The bounded restart policy.</summary>
public sealed class ModelProcessRestartPolicyTests
{
    [Fact]
    public void Defaults_WaitOneTwoAndFourSeconds_ThenGiveUp()
    {
        var policy = new ModelProcessRestartPolicy();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayBefore(3));
        Assert.Null(policy.DelayBefore(4));
        Assert.Equal(TimeSpan.FromMinutes(1), policy.StableUptime);
    }

    [Fact]
    public void Delay_IsCappedAtTheLongestWait()
    {
        var policy = new ModelProcessRestartPolicy { MaxRestarts = 10, MaxDelay = TimeSpan.FromSeconds(5) };

        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayBefore(4));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayBefore(10));
        Assert.Null(policy.DelayBefore(11));
    }

    [Fact]
    public void NoRestarts_MeansNone()
    {
        Assert.Null(new ModelProcessRestartPolicy { MaxRestarts = 0 }.DelayBefore(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelProcessRestartPolicy().DelayBefore(0));
    }
}
