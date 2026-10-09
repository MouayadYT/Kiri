using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.Ocr;

/// <summary>
/// The app's <see cref="IScreenText"/>: reads a screenshot with the <see cref="IOcrEngine"/> the first time its text is asked for,
/// keeps what was read beside the screenshot (in memory, for at most <see cref="MaxCached"/> screenshots, the oldest let go of first)
/// and lets go of it with the screenshot. Reading takes place off the caller's thread; a caller that stops waiting does not stop the read
/// for the others. It logs counts and times, never text.
/// </summary>
public sealed partial class ScreenTextService(
    IContextService contexts, IOcrEngine? engine = null, ILogger<ScreenTextService>? logger = null) : IScreenText
{
    /// <summary>How many screenshots' text is kept.</summary>
    public const int MaxCached = 16;

    /// <summary>The longest a read may take before it is given up.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Task<OcrResult?>> _cache = [];
    private readonly Queue<Guid> _order = new();
    private readonly ILogger _logger = logger ?? NullLogger<ScreenTextService>.Instance;

    /// <inheritdoc/>
    public bool IsAvailable => engine is { IsAvailable: true };

    /// <inheritdoc/>
    public bool HasScreenshot(Guid conversationId) => ScreenshotOf(conversationId) is not null;

    /// <inheritdoc/>
    public Task<OcrResult?> ReadAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        ScreenshotOf(conversationId) is { } screenshot ? ReadAsync(screenshot, cancellationToken) : Task.FromResult<OcrResult?>(null);

    /// <inheritdoc/>
    public async Task<OcrResult?> ReadAsync(ContextItem screenshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        if (engine is not { IsAvailable: true } || screenshot.ImageData.IsEmpty)
        {
            return null;
        }

        Task<OcrResult?> reading;
        lock (_gate)
        {
            if (!_cache.TryGetValue(screenshot.Id, out reading!))
            {
                reading = Task.Run(() => ReadNowAsync(engine, screenshot.ImageData));
                _cache[screenshot.Id] = reading;
                _order.Enqueue(screenshot.Id);
                while (_order.Count > MaxCached)
                {
                    _cache.Remove(_order.Dequeue());
                }
            }
        }

        var result = await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // A read that failed is not remembered: the next question tries again.
            Forget(screenshot.Id);
        }

        return result;
    }

    /// <inheritdoc/>
    public void Forget(Guid itemId)
    {
        lock (_gate)
        {
            _cache.Remove(itemId);
        }
    }

    // The conversation's screenshot: a retained one, the first the user attached, that still has its pixels.
    private ContextItem? ScreenshotOf(Guid conversationId) =>
        contexts.PendingItems(conversationId)
            .FirstOrDefault(item => item is { Type: ContextItemType.Screenshot, Retained: true } && !item.ImageData.IsEmpty);

    private async Task<OcrResult?> ReadNowAsync(IOcrEngine ocr, ReadOnlyMemory<byte> image)
    {
        var start = Stopwatch.GetTimestamp();
        using var limit = new CancellationTokenSource(ReadTimeout);
        try
        {
            var result = await ocr.RecognizeAsync(image, limit.Token).ConfigureAwait(false);
            LogRead(_logger, result.Lines.Count, result.ImageWidth, result.ImageHeight, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return result;
        }
        catch (Exception exception) when (exception is OcrUnavailableException or OcrFailedException or OperationCanceledException)
        {
            LogNotRead(_logger, exception.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(
        EventId = 2310,
        Level = LogLevel.Debug,
        Message = "Read {Lines} lines of text from a screenshot of {Width} x {Height} pixels in {ElapsedMs} ms")]
    private static partial void LogRead(ILogger logger, int lines, int width, int height, long elapsedMs);

    [LoggerMessage(EventId = 2311, Level = LogLevel.Information, Message = "The text of a screenshot could not be read ({ExceptionType})")]
    private static partial void LogNotRead(ILogger logger, string exceptionType);
}
