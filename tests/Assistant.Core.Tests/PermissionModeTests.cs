using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>Off, ask every time and allowed (PROJECT_SPEC §4.9, step 119): the settings, the decision, the approval of one use and the gate that asks.</summary>
public sealed class PermissionModeTests
{
    private static readonly PermissionCapability[] Every = Enum.GetValues<PermissionCapability>();

    private static readonly PermissionCapability[] CanAsk =
    [
        PermissionCapability.ScreenCapture, PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy,
        PermissionCapability.Calendar, PermissionCapability.Messaging, PermissionCapability.ExternalSearch,
    ];

    [Fact]
    public void OnlyTheCapabilitiesWhoseUsesAreSingleEventsCanBeAskedAbout()
    {
        Assert.Equal(CanAsk.Order(), PermissionCatalog.All.Where(definition => definition.SupportsAskEveryTime).Select(definition => definition.Capability).Order());

        // What works while the user types, keeps what is copied, or is always off has nobody to ask.
        foreach (var capability in new[] { PermissionCapability.Files, PermissionCapability.ClipboardHistory, PermissionCapability.DestructiveActions })
        {
            Assert.False(PermissionCatalog.Get(capability).SupportsAskEveryTime);
        }

        Assert.All(CanAsk, capability => Assert.False(string.IsNullOrWhiteSpace(PermissionCatalog.Get(capability).AskQuestion)));
        Assert.All(CanAsk, capability => Assert.True(PermissionCatalog.Get(capability).IsAvailable));
    }

    [Fact]
    public void AModeIsReadAndSetForEveryCapabilityThatCanAskAndOnlyThatOne()
    {
        foreach (var capability in CanAsk)
        {
            var asking = new PermissionSettings().WithMode(capability, PermissionMode.AskEveryTime);
            var allowed = asking.WithMode(capability, PermissionMode.Allowed);
            var off = asking.WithMode(capability, PermissionMode.Off);

            Assert.Equal(PermissionMode.AskEveryTime, asking.ModeOf(capability));
            Assert.Equal(PermissionMode.Allowed, allowed.ModeOf(capability));
            Assert.Equal(PermissionMode.Off, off.ModeOf(capability));
            Assert.True(asking.IsOn(capability));
            Assert.False(off.IsOn(capability));
            foreach (var other in Every.Where(other => other != capability))
            {
                Assert.Equal(new PermissionSettings().ModeOf(other), asking.ModeOf(other));
            }
        }
    }

    [Fact]
    public void ACapabilityThatCannotAskIsNeverMadeToAndAHandEditedAskForItIsRefused()
    {
        foreach (var capability in Every.Except(CanAsk))
        {
            Assert.Throws<ArgumentException>(() => new PermissionSettings().WithMode(capability, PermissionMode.AskEveryTime));
            Assert.Equal(PermissionMode.Allowed, new PermissionSettings().WithMode(capability, PermissionMode.Allowed).ModeOf(capability));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new PermissionSettings().WithMode(PermissionCapability.Files, (PermissionMode)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PermissionSettings().ModeOf((PermissionCapability)99));
    }

    [Fact]
    public void TurningASwitchOffKeepsNothingAskingAndTheModeBeforeIsNotAnAllowance()
    {
        var asking = new PermissionSettings().WithMode(PermissionCapability.Calendar, PermissionMode.AskEveryTime);

        // The switch off with the ask flag still set, as a hand-edited file could have it: off wins.
        var contradictory = asking with { Calendar = false };

        Assert.Equal(PermissionMode.Off, contradictory.ModeOf(PermissionCapability.Calendar));
        Assert.Equal(PermissionDecisionReason.TurnedOff, SettingsPermissionPolicy.Decide(contradictory, PermissionCapability.Calendar).Reason);
    }

    [Fact]
    public void TheDecisionOfACapabilitySetToAskIsThatItMustBeAskedAboutAndItIsNotAllowed()
    {
        var settings = new PermissionSettings().WithMode(PermissionCapability.ScreenCapture, PermissionMode.AskEveryTime);

        var decision = SettingsPermissionPolicy.Decide(settings, PermissionCapability.ScreenCapture);

        Assert.Equal(PermissionDecisionReason.AskEveryTime, decision.Reason);
        Assert.False(decision.IsAllowed);
        Assert.True(decision.NeedsAsking);
        Assert.True(decision.CouldBeAllowed);
        Assert.False(SettingsPermissionPolicy.Decide(new PermissionSettings { ScreenCapture = false }, PermissionCapability.ScreenCapture).CouldBeAllowed);
        Assert.True(SettingsPermissionPolicy.Decide(new PermissionSettings(), PermissionCapability.ScreenCapture).CouldBeAllowed);
    }

    [Fact]
    public async Task AnApprovalAllowsOneCapabilitySetToAskForTheWorkInsideItAndNothingElse()
    {
        var settings = new FixedSettings(new AppSettings
        {
            Permissions = new PermissionSettings().WithMode(PermissionCapability.Calendar, PermissionMode.AskEveryTime)
                .WithMode(PermissionCapability.Messaging, PermissionMode.AskEveryTime).WithMode(PermissionCapability.ExternalSearch, PermissionMode.Off),
        });
        var policy = new SettingsPermissionPolicy(settings);

        Assert.False((await policy.CheckAsync(PermissionCapability.Calendar)).IsAllowed);
        using (PermissionApprovals.Approve(PermissionCapability.Calendar))
        {
            Assert.True((await policy.CheckAsync(PermissionCapability.Calendar)).IsAllowed);

            // Only that capability: another that asks is still asked about, and one that is off is still off.
            Assert.False((await policy.CheckAsync(PermissionCapability.Messaging)).IsAllowed);
            Assert.Equal(PermissionDecisionReason.TurnedOff, (await policy.CheckAsync(PermissionCapability.ExternalSearch)).Reason);
        }

        // It ends with its scope.
        Assert.Equal(PermissionDecisionReason.AskEveryTime, (await policy.CheckAsync(PermissionCapability.Calendar)).Reason);
    }

    [Fact]
    public async Task AnApprovalCannotRaiseACapabilityThatIsOffOrThatTheBuildCannotDo()
    {
        var policy = new SettingsPermissionPolicy(new FixedSettings(new AppSettings { Permissions = new PermissionSettings { Calendar = false, DestructiveActions = true } }));

        using (PermissionApprovals.Approve(PermissionCapability.Calendar))
        using (PermissionApprovals.Approve(PermissionCapability.DestructiveActions))
        {
            Assert.Equal(PermissionDecisionReason.TurnedOff, (await policy.CheckAsync(PermissionCapability.Calendar)).Reason);
            Assert.Equal(PermissionDecisionReason.NotAvailable, (await policy.CheckAsync(PermissionCapability.DestructiveActions)).Reason);
        }
    }

    [Fact]
    public async Task AnApprovalFollowsTheWorkItStartsAndIsNotSeenByWorkThatIsNotInsideIt()
    {
        var policy = new SettingsPermissionPolicy(new FixedSettings(new AppSettings
        {
            Permissions = new PermissionSettings().WithMode(PermissionCapability.Calendar, PermissionMode.AskEveryTime),
        }));
        var elsewhere = Task.Run(async () =>
        {
            await Task.Delay(50);
            return await policy.CheckAsync(PermissionCapability.Calendar);
        });

        PermissionDecision inside;
        using (PermissionApprovals.Approve(PermissionCapability.Calendar))
        {
            // Several awaits and a thread of the pool later, the work is still inside it.
            inside = await Task.Run(async () =>
            {
                await Task.Delay(10);
                return await policy.CheckAsync(PermissionCapability.Calendar);
            });
        }

        Assert.True(inside.IsAllowed);
        Assert.False((await elsewhere).IsAllowed);
        Assert.False(PermissionApprovals.IsApproved(PermissionCapability.Calendar));
    }

    [Fact]
    public void AScopeCanBeDisposedTwiceAndScopesNest()
    {
        var outer = PermissionApprovals.Approve(PermissionCapability.Calendar);
        var inner = PermissionApprovals.Approve(PermissionCapability.Messaging);
        Assert.True(PermissionApprovals.IsApproved(PermissionCapability.Calendar));
        Assert.True(PermissionApprovals.IsApproved(PermissionCapability.Messaging));

        inner.Dispose();
        inner.Dispose();

        Assert.True(PermissionApprovals.IsApproved(PermissionCapability.Calendar));
        Assert.False(PermissionApprovals.IsApproved(PermissionCapability.Messaging));
        outer.Dispose();
        Assert.False(PermissionApprovals.IsApproved(PermissionCapability.Calendar));
    }

    // ---- the gate ------------------------------------------------------------------------------------------------------

    private static (PermissionGate Gate, FakePrompt Prompt, SettingsPermissionPolicy Policy) GateFor(PermissionSettings permissions, ConfirmationDecision? answer = ConfirmationDecision.Approved)
    {
        var policy = new SettingsPermissionPolicy(new FixedSettings(new AppSettings { Permissions = permissions }));
        var prompt = new FakePrompt { Answer = answer ?? ConfirmationDecision.Approved };
        return (new PermissionGate(policy, answer is null ? null : prompt, NullLogger<PermissionGate>.Instance), prompt, policy);
    }

    private static readonly PermissionSettings Asking = new PermissionSettings().WithMode(PermissionCapability.ScreenCapture, PermissionMode.AskEveryTime);

    [Fact]
    public async Task ACapabilityThatIsAllowedGoesAheadWithoutAQuestion()
    {
        var (gate, prompt, _) = GateFor(new PermissionSettings());

        var grant = await gate.RequestAsync(PermissionCapability.ScreenCapture);

        Assert.True(grant.IsGranted);
        Assert.False(grant.ApprovedByUser);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task ACapabilityThatIsOffOrNotAvailableIsRefusedWithoutAQuestion()
    {
        var (gate, prompt, _) = GateFor(new PermissionSettings { ScreenCapture = false });

        var off = await gate.RequestAsync(PermissionCapability.ScreenCapture);
        var unavailable = await gate.RequestAsync(PermissionCapability.DestructiveActions);

        Assert.False(off.IsGranted);
        Assert.Equal(PermissionDecisionReason.TurnedOff, off.Decision.Reason);
        Assert.Equal(PermissionDecisionReason.NotAvailable, unavailable.Decision.Reason);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task ACapabilitySetToAskIsAskedAboutAndAYesAllowsThatUseThroughThePolicy()
    {
        var (gate, prompt, policy) = GateFor(Asking);

        var grant = await gate.RequestAsync(PermissionCapability.ScreenCapture, "You used the shortcut.");

        Assert.True(grant.IsGranted);
        Assert.True(grant.ApprovedByUser);
        var asked = Assert.Single(prompt.Asked);
        Assert.Equal(PermissionCapability.ScreenCapture, asked.Capability);
        Assert.Equal(PermissionCatalog.Get(PermissionCapability.ScreenCapture).AskQuestion, asked.Question);
        Assert.Equal("You used the shortcut.", asked.Detail);

        // The yes is for the work that follows it, which services see through the policy; outside it nothing is allowed.
        Assert.False((await policy.CheckAsync(PermissionCapability.ScreenCapture)).IsAllowed);
        using (grant.Enter())
        {
            Assert.True((await policy.CheckAsync(PermissionCapability.ScreenCapture)).IsAllowed);
        }

        Assert.False((await policy.CheckAsync(PermissionCapability.ScreenCapture)).IsAllowed);
    }

    [Fact]
    public async Task ASecondUseIsAskedAboutAgainAYesIsNeverKept()
    {
        var (gate, prompt, _) = GateFor(Asking);

        _ = await gate.RequestAsync(PermissionCapability.ScreenCapture);
        _ = await gate.RequestAsync(PermissionCapability.ScreenCapture);

        Assert.Equal(2, prompt.Asked.Count);
    }

    [Fact]
    public async Task ANoOrNoAnswerOrNoQuestionIsAnAnswerOfNo()
    {
        foreach (var (answer, reason) in new[]
                 {
                     (ConfirmationDecision.Declined, PermissionDecisionReason.Declined),
                     (ConfirmationDecision.NoAnswer, PermissionDecisionReason.CouldNotAsk),
                     (ConfirmationDecision.CouldNotAsk, PermissionDecisionReason.CouldNotAsk),
                 })
        {
            var (gate, _, policy) = GateFor(Asking, answer);

            var grant = await gate.RequestAsync(PermissionCapability.ScreenCapture);

            Assert.False(grant.IsGranted);
            Assert.False(grant.ApprovedByUser);
            Assert.Equal(reason, grant.Decision.Reason);
            using (grant.Enter())
            {
                Assert.False((await policy.CheckAsync(PermissionCapability.ScreenCapture)).IsAllowed);
            }
        }

        var (withoutPrompt, _, _) = GateFor(Asking, answer: null);
        var none = await withoutPrompt.RequestAsync(PermissionCapability.ScreenCapture);
        Assert.False(none.IsGranted);
        Assert.Equal(PermissionDecisionReason.CouldNotAsk, none.Decision.Reason);
    }

    [Fact]
    public async Task AQuestionThatFailsIsNotAYesAndACancelledOneIsTakenBack()
    {
        var (gate, prompt, _) = GateFor(Asking);
        prompt.Throws = new InvalidOperationException();

        var grant = await gate.RequestAsync(PermissionCapability.ScreenCapture);

        Assert.False(grant.IsGranted);
        Assert.Equal(PermissionDecisionReason.CouldNotAsk, grant.Decision.Reason);

        prompt.Throws = null;
        using var cancel = new CancellationTokenSource();
        prompt.OnAsk = () => cancel.Cancel();
        prompt.ThrowOnCancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.RequestAsync(PermissionCapability.ScreenCapture, null, cancel.Token));
    }

    [Fact]
    public async Task AUseInsideAnApprovedWorkIsNotAskedAboutAgain()
    {
        var (gate, prompt, _) = GateFor(Asking);
        using var approval = PermissionApprovals.Approve(PermissionCapability.ScreenCapture);

        var grant = await gate.RequestAsync(PermissionCapability.ScreenCapture);

        Assert.True(grant.IsGranted);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public void TheWordsSaySoPlainlyForEveryReasonAndNameTheCapability()
    {
        foreach (var capability in CanAsk)
        {
            var title = PermissionCatalog.Get(capability).Title;
            foreach (var reason in Enum.GetValues<PermissionDecisionReason>().Where(reason => reason != PermissionDecisionReason.Granted))
            {
                var decision = new PermissionDecision(capability, reason);
                Assert.Contains(title, PermissionTexts.WhyNot(decision), StringComparison.Ordinal);
                Assert.Contains(title, PermissionTexts.ForModel(decision), StringComparison.Ordinal);
            }
        }

        Assert.Contains("turned off", PermissionTexts.ForModel(new PermissionDecision(PermissionCapability.Calendar, PermissionDecisionReason.TurnedOff)), StringComparison.Ordinal);
        Assert.Contains("did not allow", PermissionTexts.ForModel(new PermissionDecision(PermissionCapability.Calendar, PermissionDecisionReason.Declined)), StringComparison.Ordinal);
    }

    private sealed class FakePrompt : IPermissionPrompt
    {
        public ConfirmationDecision Answer { get; set; } = ConfirmationDecision.Approved;

        public Exception? Throws { get; set; }

        public Action? OnAsk { get; set; }

        public bool ThrowOnCancel { get; set; }

        public List<PermissionPromptRequest> Asked { get; } = [];

        public Task<ConfirmationDecision> AskAsync(PermissionPromptRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            OnAsk?.Invoke();
            if (ThrowOnCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Throws is null ? Task.FromResult(Answer) : Task.FromException<ConfirmationDecision>(Throws);
        }
    }
}
