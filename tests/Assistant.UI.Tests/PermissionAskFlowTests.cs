using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.UI.Permissions;
using Assistant.UI.Selection;
using Assistant.Windows.Selection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- A permission set to ask every time, from the shortcuts that use it (PROJECT_SPEC §4.9, step 119) --------------------------

    private sealed class RecordingPrompt(params ConfirmationDecision[] answers) : IPermissionPrompt
    {
        private int _next;

        public List<PermissionPromptRequest> Asked { get; } = [];

        public Task<ConfirmationDecision> AskAsync(PermissionPromptRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            var answer = answers.Length == 0 ? ConfirmationDecision.Approved : answers[Math.Min(_next, answers.Length - 1)];
            _next++;
            return Task.FromResult(answer);
        }
    }

    private static IPermissionGate AskingGate(RecordingPrompt prompt, params PermissionCapability[] asking)
    {
        var settings = new InMemorySettingsService();
        var permissions = asking.Aggregate(new PermissionSettings(), (current, capability) => current.WithMode(capability, PermissionMode.AskEveryTime));
        settings.SaveAsync(new AppSettings { Permissions = permissions }).GetAwaiter().GetResult();
        return new PermissionGate(new SettingsPermissionPolicy(settings), prompt, NullLogger<PermissionGate>.Instance);
    }

    [Fact]
    public void SelectedTextSetToAskIsAskedAboutAndOnlyAYesReadsTheSelectionUnderThatYes() => RunSta(() =>
    {
        var prompt = new RecordingPrompt();
        var setup = CreateAskSelection(gate: AskingGate(prompt, PermissionCapability.SelectedText));

        Press(setup);

        var asked = Assert.Single(prompt.Asked);
        Assert.Equal(PermissionCapability.SelectedText, asked.Capability);
        Assert.DoesNotContain(SampleSelection, asked.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, setup.Service.Calls);
        Assert.True(setup.Service.ApprovedAtRead);
        Assert.Equal(SampleSelection, Assert.Single(setup.Conversation.Texts).Text);
    });

    [Theory]
    [InlineData(ConfirmationDecision.Declined)]
    [InlineData(ConfirmationDecision.NoAnswer)]
    [InlineData(ConfirmationDecision.CouldNotAsk)]
    public void WithoutAYesTheSelectionIsNeverReadAndTheUserIsToldNothingWasRead(ConfirmationDecision answer) => RunSta(() =>
    {
        var prompt = new RecordingPrompt(answer);
        var setup = CreateAskSelection(gate: AskingGate(prompt, PermissionCapability.SelectedText));

        Press(setup);

        Assert.Single(prompt.Asked);
        Assert.Equal(0, setup.Service.Calls);
        Assert.Empty(setup.Conversation.Texts);
        var message = Assert.Single(setup.Conversation.Messages);
        Assert.Contains("Selected Text", message.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing was read", message.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void ASecondPressIsAskedAboutAgain() => RunSta(() =>
    {
        var prompt = new RecordingPrompt();
        var setup = CreateAskSelection(gate: AskingGate(prompt, PermissionCapability.SelectedText));

        Press(setup);
        Press(setup);

        Assert.Equal(2, prompt.Asked.Count);
        Assert.Equal(2, setup.Service.Calls);
        Assert.False(PermissionApprovals.IsApproved(PermissionCapability.SelectedText));
    });

    [Fact]
    public void ASelectedTextThatIsAllowedIsReadWithoutAQuestion() => RunSta(() =>
    {
        var prompt = new RecordingPrompt(ConfirmationDecision.Declined);
        var setup = CreateAskSelection(gate: AskingGate(prompt));

        Press(setup);

        Assert.Empty(prompt.Asked);
        Assert.Equal(1, setup.Service.Calls);
    });

    [Fact]
    public void TheCopyShortcutAsksAboutBothPermissionsAndPressesCopyUnderBothYeses() => RunSta(() =>
    {
        var prompt = new RecordingPrompt();
        var setup = CreateAskByCopy(gate: AskingGate(prompt, PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy));

        PressCopy(setup);

        Assert.Equal([PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy], prompt.Asked.Select(asked => asked.Capability));
        Assert.Equal(1, setup.Copy.Calls);
        Assert.True(setup.Copy.SelectedTextApproved);
        Assert.True(setup.Copy.ByCopyApproved);
    });

    [Fact]
    public void ANoToPressingCopyMeansNothingIsSentToTheAppAndTheClipboardIsNotTouched() => RunSta(() =>
    {
        var prompt = new RecordingPrompt(ConfirmationDecision.Approved, ConfirmationDecision.Declined);
        var setup = CreateAskByCopy(gate: AskingGate(prompt, PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy));

        PressCopy(setup);

        Assert.Equal(2, prompt.Asked.Count);
        Assert.Equal(0, setup.Copy.Calls);
        Assert.Empty(setup.Conversation.Texts);
        Assert.Contains("Selected Text by Copy", Assert.Single(setup.Conversation.Messages).Text, StringComparison.Ordinal);
    });

    [Fact]
    public void ScreenCaptureSetToAskIsAskedAboutBeforeTheScreenIsTouchedAndTheCaptureRunsUnderThatYes() => RunSta(() =>
    {
        var prompt = new RecordingPrompt();
        var setup = CreateVisual(gate: AskingGate(prompt, PermissionCapability.ScreenCapture));

        var running = Run(setup, null);
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        var asked = Assert.Single(prompt.Asked);
        Assert.Equal(PermissionCapability.ScreenCapture, asked.Capability);
        Assert.Equal(1, setup.Capture.Captures);
        Assert.True(setup.Capture.ApprovedAtCapture);

        // The yes covered the capture and nothing after it: the overlay, and what the part chosen starts, run without it.
        Assert.False(setup.Overlay.ApprovedAtSelect);
        Assert.False(PermissionApprovals.IsApproved(PermissionCapability.ScreenCapture));
        Assert.Equal(["hide", "capture", "overlay"], setup.Order);
    });

    [Fact]
    public void ANoToACaptureMeansNothingIsCapturedAndTheWindowIsNotHidden() => RunSta(() =>
    {
        var prompt = new RecordingPrompt(ConfirmationDecision.Declined);
        var setup = CreateVisual(gate: AskingGate(prompt, PermissionCapability.ScreenCapture));

        var running = Run(setup, null);
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(0, setup.Capture.Captures);
        Assert.Equal(0, setup.Window.HiddenAtOnce);
        var message = Assert.Single(setup.Conversation.Messages);
        Assert.Contains("Screen Capture", message.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing was captured", message.Text, StringComparison.Ordinal);
    });

    // ---- the window that asks ------------------------------------------------------------------------------------------------------

    private static PermissionPromptRequest Request(string? detail = "You used the shortcut.") =>
        new(PermissionCapability.ScreenCapture, "Let the Assistant capture part of your screen?", detail);

    [Fact]
    public void ThePromptWindowSaysWhatItAsksAndOnlyAllowOnceApprovesAndEnterAndEscapeNeverDo() => RunSta(() => WithTheme(() =>
    {
        var window = new PermissionPromptWindow(Request(), armDelay: TimeSpan.Zero, lifetime: TimeSpan.FromMinutes(5));
        try
        {
            window.Show();
            Pump();

            Assert.Equal("Let the Assistant capture part of your screen?", Named<System.Windows.Controls.TextBlock>(window, "Heading").Text);
            Assert.Equal("You used the shortcut.", Named<System.Windows.Controls.TextBlock>(window, "Detail").Text);
            Assert.Contains("Settings", Named<System.Windows.Controls.TextBlock>(window, "Setting").Text, StringComparison.Ordinal);
            // Don't allow is the default button and the cancel button: the keys that close a dialog never allow.
            Assert.True(window.DeclineChoice.IsDefault);
            Assert.True(window.DeclineChoice.IsCancel);
            Assert.False(window.AllowChoice.IsDefault);
            Assert.True(window.AllowChoice.IsEnabled);
            Assert.Equal(ConfirmationDecision.Declined, window.Decision);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void AllowOnceDoesNotWorkUntilTheWindowHasBeenInFrontOfTheUserForAMoment() => RunSta(() => WithTheme(() =>
    {
        var window = new PermissionPromptWindow(Request(), armDelay: TimeSpan.FromMinutes(1), lifetime: TimeSpan.FromMinutes(5));
        try
        {
            window.Show();
            Pump();

            Assert.False(window.AllowChoice.IsEnabled);
            window.AllowChoice.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal(ConfirmationDecision.Declined, window.Decision);
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void PressingAllowOnceApprovesAndPressingDontAllowDeclines() => RunSta(() => WithTheme(() =>
    {
        var allowed = new PermissionPromptWindow(Request(), armDelay: TimeSpan.Zero, lifetime: TimeSpan.FromMinutes(5));
        allowed.Show();
        Pump();
        allowed.AllowChoice.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(ConfirmationDecision.Approved, allowed.Decision);

        var refused = new PermissionPromptWindow(Request(null), armDelay: TimeSpan.Zero, lifetime: TimeSpan.FromMinutes(5));
        refused.Show();
        Pump();
        refused.DeclineChoice.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(ConfirmationDecision.Declined, refused.Decision);
        Assert.Equal(System.Windows.Visibility.Collapsed, Named<System.Windows.Controls.TextBlock>(refused, "Detail").Visibility);
    }));

    [Fact]
    public void AWindowThatIsNotAnsweredInTimeClosesWithNoAnswer() => RunSta(() => WithTheme(() =>
    {
        var window = new PermissionPromptWindow(Request(), armDelay: TimeSpan.Zero, lifetime: TimeSpan.FromMilliseconds(50));
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Show();
        WaitUntil(() => closed, "The question did not give up.");

        Assert.Equal(ConfirmationDecision.NoAnswer, window.Decision);
    }));
}
