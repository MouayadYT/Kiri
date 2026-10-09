using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Assistant.Core.Contracts;
using Assistant.Core.Diagnostics;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Settings the test fixes, and can change between turns.</summary>
internal sealed class FixedSettings(AppSettings? settings = null) : ISettingsService
{
    public AppSettings Current { get; set; } = settings ?? new AppSettings { ContextLimits = new ContextLimitSettings() };

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

/// <summary>A model whose answer the test writes, chunk by chunk, and whose requests it records.</summary>
internal sealed class ScriptedModel : IModelService
{
    private readonly Channel<AssistantResponseChunk> _chunks = Channel.CreateUnbounded<AssistantResponseChunk>();
    private Exception? _failure;

    public ModelInfo? Active { get; init; } = new("test-model", 4096);

    public List<ModelRequest> Requests { get; } = [];

    public void Write(string text) => _chunks.Writer.TryWrite(AssistantResponseChunk.ForTextDelta(text));

    public void Write(AssistantResponseChunk chunk) => _chunks.Writer.TryWrite(chunk);

    public void End(Exception? failure = null)
    {
        _failure = failure;
        _chunks.Writer.TryComplete();
    }

    public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);

    public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }

        if (_failure is not null)
        {
            throw _failure;
        }
    }
}

/// <summary>
/// Prepares images as the test says, recording each one it is given: by default it hands back a copy with its first byte
/// flipped, so a test can tell the prepared image from the original, and it cannot read an image that starts with "BAD".
/// </summary>
internal sealed class FakeImagePreprocessor : IImagePreprocessor
{
    public List<byte[]> Given { get; } = [];

    /// <summary>What each image it was given showed, when it was told.</summary>
    public List<ImageContent> Contents { get; } = [];

    public Task<PreparedImage> PrepareAsync(
        ReadOnlyMemory<byte> image, ImageContent content, CancellationToken cancellationToken = default)
    {
        Contents.Add(content);
        return PrepareAsync(image, cancellationToken);
    }

    public Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Given.Add(image.ToArray());
        if (image.Span.StartsWith("BAD"u8))
        {
            throw new ImagePreprocessingException();
        }

        var prepared = image.ToArray();
        prepared[0] ^= 0xFF;
        return Task.FromResult(new PreparedImage(prepared, ImageFormat.Png, 10, 5, 20, 10));
    }
}

/// <summary>Keeps every log entry at every level: the formatted text, each structured value and the exception.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public string AllText => string.Join(Environment.NewLine, _entries);

    /// <summary>A factory whose loggers write here, through the privacy filter when asked.</summary>
    public ILoggerFactory CreateFactory(bool privacyFilter = false) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(this);
            if (privacyFilter)
            {
                builder.AddPrivacyFilter();
            }
        });

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            owner._entries.Enqueue($"{category} {logLevel} {eventId.Id} {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (name, value) in values)
                {
                    owner._entries.Enqueue($"  {name} = {value}");
                }
            }
        }
    }
}
