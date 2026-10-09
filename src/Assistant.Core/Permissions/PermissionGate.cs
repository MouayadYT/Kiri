using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Permissions;

/// <summary>
/// The app's <see cref="IPermissionGate"/> (PROJECT_SPEC §4.9, step 119): the policy decides, and when the capability is set to ask every time the user is asked, once for
/// this use. A use that nobody could ask about is a no, never a yes. It logs the capability and the outcome, never what the use was for.
/// </summary>
public sealed partial class PermissionGate : IPermissionGate
{
    private readonly IPermissionPolicy _policy;
    private readonly IPermissionPrompt? _prompt;
    private readonly ILogger<PermissionGate> _logger;

    /// <summary>Creates the gate.</summary>
    /// <param name="policy">What decides whether the switch allows, asks or refuses.</param>
    /// <param name="prompt">What shows the question; without it a capability set to ask each time cannot be asked about, so it is refused.</param>
    /// <param name="logger">Where the outcome is logged.</param>
    public PermissionGate(IPermissionPolicy policy, IPermissionPrompt? prompt, ILogger<PermissionGate> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        _policy = policy;
        _prompt = prompt;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PermissionGrant> RequestAsync(PermissionCapability capability, string? detail = null, CancellationToken cancellationToken = default)
    {
        var decision = await _policy.CheckAsync(capability, cancellationToken).ConfigureAwait(false);
        if (!decision.NeedsAsking)
        {
            return new PermissionGrant(decision, approvedByUser: false);
        }

        if (_prompt is null)
        {
            LogOutcome(_logger, capability, PermissionDecisionReason.CouldNotAsk);
            return new PermissionGrant(new PermissionDecision(capability, PermissionDecisionReason.CouldNotAsk), approvedByUser: false);
        }

        ConfirmationDecision answer;
        try
        {
            var definition = PermissionCatalog.Get(capability);
            var question = definition.AskQuestion.Length > 0 ? definition.AskQuestion : $"Let the Assistant use {definition.Title}?";
            answer = await _prompt.AskAsync(new PermissionPromptRequest(capability, question, detail), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A question that could not be asked is not a yes.
            answer = ConfirmationDecision.CouldNotAsk;
        }

        var reason = answer switch
        {
            ConfirmationDecision.Approved => PermissionDecisionReason.Granted,
            ConfirmationDecision.Declined => PermissionDecisionReason.Declined,
            _ => PermissionDecisionReason.CouldNotAsk,
        };
        LogOutcome(_logger, capability, reason);
        return new PermissionGrant(new PermissionDecision(capability, reason), approvedByUser: reason == PermissionDecisionReason.Granted);
    }

    [LoggerMessage(EventId = 3220, Level = LogLevel.Information, Message = "A use of {Capability} that is asked about each time ended with {Reason}")]
    private static partial void LogOutcome(ILogger logger, PermissionCapability capability, PermissionDecisionReason reason);
}
