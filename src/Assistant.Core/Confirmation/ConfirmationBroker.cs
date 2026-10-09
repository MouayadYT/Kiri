using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Confirmation;

/// <summary>
/// The app's <see cref="IPermissionService"/> (PROJECT_SPEC §4.8, step 115): asks the user, in the conversation the call was made in, whether a tool that
/// changes something may do exactly what it is about to do, and lets it run only on a yes. It publishes a <see cref="ToolConfirmationRequested"/>; the surface
/// that shows the conversation puts the question in front of the user (<see cref="ToolConfirmationRequested.MarkShown"/>) and the user's click answers it.
/// </summary>
/// <remarks>
/// <para>
/// Every way the question can fail to be answered is a no, never a yes: nothing showed it (<see cref="ConfirmationDecision.CouldNotAsk"/>), the user did not
/// answer within <see cref="Lifetime"/> (<see cref="ConfirmationDecision.NoAnswer"/>), the answer was stopped (the question is taken back), or publishing it failed. An
/// answer is given once to one request: a yes cannot be kept for later or used for another call.
/// </para>
/// <para>It logs the kind of what was asked, the decision and how long it took, never what the call was or said (PROJECT_SPEC §3.3).</para>
/// </remarks>
public sealed partial class ConfirmationBroker : IPermissionService
{
    /// <summary>How long a question waits for the user: after this a question that was not answered is withdrawn and the call is not made.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private readonly IAppEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConfirmationBroker> _logger;
    private readonly ISettingsService? _settings;

    /// <summary>Creates the broker.</summary>
    /// <param name="bus">Where the question is published; whichever surface shows the conversation listens for it.</param>
    /// <param name="clock">The time of the question's lifetime.</param>
    /// <param name="logger">Where it says how a question ended.</param>
    /// <param name="lifetime">How long a question waits for the user, or <see langword="null"/> for <see cref="DefaultLifetime"/>.</param>
    /// <param name="settings">
    /// Where "always allow" is kept (<see cref="StandingApprovals"/>). Without it no question offers that answer, and every call is asked about.
    /// </param>
    public ConfirmationBroker(
        IAppEventBus bus, TimeProvider clock, ILogger<ConfirmationBroker> logger, TimeSpan? lifetime = null, ISettingsService? settings = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        var wait = lifetime ?? DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero);
        _bus = bus;
        _clock = clock;
        _logger = logger;
        _settings = settings;
        Lifetime = wait;
    }

    /// <summary>How long a question waits for the user.</summary>
    public TimeSpan Lifetime { get; }

    /// <inheritdoc/>
    public async Task<ConfirmationDecision> ConfirmToolCallAsync(
        ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(confirmation);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();

        // An action the user said is always allowed is not asked about again, while it is what it was then. Which questions can be answered that way
        // is decided here, by fixed rules, each time: never a message, whatever the settings hold.
        var mayBeKept = _settings is not null && StandingApprovals.MayBeKept(tool, confirmation);
        if (mayBeKept && await IsKeptAsync(tool, cancellationToken).ConfigureAwait(false))
        {
            LogAlwaysAllowed(_logger, confirmation.Kind);
            return ConfirmationDecision.Approved;
        }

        var request = new ToolConfirmationRequested(tool, call, context.ConversationId, confirmation, mayBeKept);

        try
        {
            await _bus.PublishAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.Withdraw();
            throw;
        }
        catch (Exception)
        {
            // A question that could not be published is a question nobody saw.
            request.Withdraw();
            return Ended(confirmation, ConfirmationDecision.CouldNotAsk, started);
        }

        // Nothing showed it (no window holds the conversation, or it is not on screen): it cannot be answered, so it is not asked and nothing is done.
        if (!request.IsShown)
        {
            request.Withdraw();
            return Ended(confirmation, ConfirmationDecision.CouldNotAsk, started);
        }

        using var timeUp = new CancellationTokenSource(Lifetime, _clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeUp.Token);
        try
        {
            await request.Settled.WaitAsync(either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                request.Withdraw();
                throw;
            }

            // The time is up. An answer that came at that very moment still counts: the first one wins.
            request.Expire();
        }

        var decision = request.State switch
        {
            ToolConfirmationState.Approved => ConfirmationDecision.Approved,
            ToolConfirmationState.Declined => ConfirmationDecision.Declined,
            _ => ConfirmationDecision.NoAnswer,
        };

        // "Always allow" is this yes, kept. The call is made whether or not it could be kept: the user said yes to it.
        if (request.IsAlways && mayBeKept)
        {
            await KeepAsync(tool).ConfigureAwait(false);
        }

        return Ended(confirmation, decision, started);
    }

    private async Task<bool> IsKeptAsync(ToolDefinition tool, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings!.LoadAsync(cancellationToken).ConfigureAwait(false);
            return StandingApprovals.IsKept(settings.Permissions, tool);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Settings that cannot be read allow nothing: the user is asked.
            return false;
        }
    }

    private async Task KeepAsync(ToolDefinition tool)
    {
        try
        {
            var saved = await _settings!.UpdateAsync(settings => settings with { Permissions = StandingApprovals.Keep(settings.Permissions, tool) })
                .ConfigureAwait(false);
            await _bus.PublishAsync(new SettingsSaved(saved)).ConfigureAwait(false);
            LogKept(_logger);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // It could not be kept: the next call is asked about, which is the safe way to be wrong.
            LogNotKept(_logger, exception.GetType().Name);
        }
    }

    private ConfirmationDecision Ended(ToolConfirmation confirmation, ConfirmationDecision decision, long started)
    {
        LogAsked(_logger, confirmation.Kind, decision, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return decision;
    }

    [LoggerMessage(EventId = 3210, Level = LogLevel.Information, Message = "Confirmation of a {Kind} call ended with {Decision} after {ElapsedMs} ms")]
    private static partial void LogAsked(ILogger logger, ConfirmationKind kind, ConfirmationDecision decision, long elapsedMs);

    [LoggerMessage(EventId = 3211, Level = LogLevel.Information, Message = "A {Kind} call was made without asking: the user chose to always allow that action")]
    private static partial void LogAlwaysAllowed(ILogger logger, ConfirmationKind kind);

    [LoggerMessage(EventId = 3212, Level = LogLevel.Information, Message = "The user chose to always allow an action; it is kept in the settings")]
    private static partial void LogKept(ILogger logger);

    [LoggerMessage(EventId = 3213, Level = LogLevel.Warning, Message = "An action the user chose to always allow could not be kept ({Reason}); it will be asked about again")]
    private static partial void LogNotKept(ILogger logger, string reason);
}
