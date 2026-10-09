using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Search.Planning;

/// <summary>
/// The model's part in finding files (PROJECT_SPEC §4.7): when no name holds every word of a request, the local model is given the
/// request and the names that hold some of them (<see cref="ReviewPrompt"/>), and answers with JSON that holds only the numbers of
/// the ones the request could mean. The reply is validated (<see cref="ReviewReplyParser"/>) and can only point into the list, so
/// it cannot name a file, a folder or a query of its own; a missing, late or invalid reply is "could not judge". The names go only
/// to the local model, which runs on this PC, and only after the Files permission was checked by the caller.
/// </summary>
/// <remarks>
/// The model is asked through <see cref="IModelService"/>, so the Searching chip says "Thinking" while it loads and answers. A
/// request, a name, a reply and a pick are never logged: the log line holds the number of candidates, why the reply was no use, and
/// a duration.
/// </remarks>
public sealed class ModelFileMatchReviewer : IFileMatchReviewer
{
    /// <summary>How long the model has to choose before nothing is picked.</summary>
    public static readonly TimeSpan DefaultModelTimeout = TimeSpan.FromSeconds(45);

    // A pick is a few numbers of JSON; this leaves room for a reasoning model's few words without letting one run on.
    private const int MaxOutputTokens = 300;

    private readonly IModelService _models;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the reviewer over the local model, dating a file by <paramref name="clock"/>'s time zone.</summary>
    public ModelFileMatchReviewer(IModelService models, TimeProvider clock, ILogger<ModelFileMatchReviewer> logger)
        : this(models, clock, logger, DefaultModelTimeout)
    {
    }

    internal ModelFileMatchReviewer(IModelService models, TimeProvider clock, ILogger logger, TimeSpan timeout)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResultItem>?> ChooseAsync(
        string request,
        IReadOnlyList<SearchResultItem> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        var cleaned = RequestText.Clean(request);
        if (candidates.Count == 0)
        {
            return [];
        }

        if (cleaned.Length == 0)
        {
            return null;
        }

        var clock = Stopwatch.StartNew();
        var shown = candidates.Take(ReviewPrompt.MaxCandidates).ToArray();
        var prompt = new ModelRequest(
            ReviewPrompt.Instructions,
            [new Message(Guid.NewGuid(), MessageRole.User, ReviewPrompt.Question(cleaned, shown, _clock.LocalTimeZone), _clock.GetUtcNow())])
        {
            MaxOutputTokens = MaxOutputTokens,
        };

        var reply = await ModelJsonAsker.AskAsync(_models, prompt, _timeout, _logger, cancellationToken).ConfigureAwait(false);
        var picked = reply.Json is { } json ? ReviewReplyParser.Parse(json, shown.Length) : null;
        if (picked is null)
        {
            PlanningLog.ReviewUnusable(_logger, reply.Json is null ? reply.Failure : ModelReplyFailure.InvalidReply, shown.Length, clock.ElapsedMilliseconds);
            return null;
        }

        return [.. picked.Select(number => shown[number - 1])];
    }
}
