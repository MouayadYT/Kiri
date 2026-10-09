using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;
using Assistant.Windows.Selection;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>The screen capture and the reading of a selection behind their permissions, in the services themselves (PROJECT_SPEC §4.5, §4.6, §4.9, step 119).</summary>
public sealed class PermissionCheckedServicesTests
{
    private sealed class Policy(params PermissionCapability[] allowed) : IPermissionPolicy
    {
        public List<PermissionCapability> Asked { get; } = [];

        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default)
        {
            Asked.Add(capability);
            return Task.FromResult(new PermissionDecision(capability, allowed.Contains(capability) ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
        }
    }

    // A capture service that records what it was asked and never takes a picture: reaching it is the whole answer.
    private sealed class Capture : IScreenCapture
    {
        private static readonly CaptureMonitor Monitor = new(0, new ScreenRect(0, 0, 100, 100), 96, true);

        public int Reached { get; private set; }

        public IReadOnlyList<CaptureMonitor> GetMonitors() => [Monitor];

        public Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default) => Reach<CapturedImage>();

        public Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default) => Reach<IReadOnlyList<CapturedImage>>();

        public Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default) => Reach<CapturedImage>();

        public Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default) => Reach<CapturedImage>();

        private Task<T> Reach<T>()
        {
            Reached++;
            throw new ScreenCaptureException(ScreenCaptureFailure.NothingToCapture);
        }
    }

    private static async Task<ScreenCaptureFailure> FailureOf(Func<Task> capture) => (await Assert.ThrowsAsync<ScreenCaptureException>(capture)).Failure;

    [Fact]
    public async Task NoKindOfCaptureReachesTheScreenWhileScreenCaptureIsNotAllowed()
    {
        var inner = new Capture();
        var service = new PermissionCheckedScreenCapture(inner, new Policy());

        Assert.Equal(ScreenCaptureFailure.NotAllowed, await FailureOf(() => service.CaptureAllMonitorsAsync()));
        Assert.Equal(ScreenCaptureFailure.NotAllowed, await FailureOf(() => service.CaptureMonitorAsync(inner.GetMonitors()[0])));
        Assert.Equal(ScreenCaptureFailure.NotAllowed, await FailureOf(() => service.CaptureWindowAsync(1)));
        Assert.Equal(ScreenCaptureFailure.NotAllowed, await FailureOf(() => service.CaptureRegionAsync(new ScreenRect(0, 0, 10, 10))));
        Assert.Equal(0, inner.Reached);
    }

    [Fact]
    public async Task AllowedCapturesReachTheScreenAndListingTheMonitorsNeedsNoPermission()
    {
        var inner = new Capture();
        var policy = new Policy(PermissionCapability.ScreenCapture);
        var service = new PermissionCheckedScreenCapture(inner, policy);

        Assert.Equal(ScreenCaptureFailure.NothingToCapture, await FailureOf(() => service.CaptureAllMonitorsAsync()));
        Assert.Equal(ScreenCaptureFailure.NothingToCapture, await FailureOf(() => service.CaptureRegionAsync(new ScreenRect(0, 0, 10, 10))));
        Assert.Equal(2, inner.Reached);
        Assert.Single(service.GetMonitors());
        Assert.All(policy.Asked, capability => Assert.Equal(PermissionCapability.ScreenCapture, capability));
    }

    [Fact]
    public async Task ASetToAskCaptureIsRefusedUnlessTheUserHasJustSaidYesForTheWorkInsideIt()
    {
        var asking = new SettingsPermissionPolicy(new Fixed(
            PermissionSettingsExtensions.WithMode(new Assistant.Core.Settings.PermissionSettings(), PermissionCapability.ScreenCapture, PermissionMode.AskEveryTime)));
        var inner = new Capture();
        var service = new PermissionCheckedScreenCapture(inner, asking);

        Assert.Equal(ScreenCaptureFailure.NotAllowed, await FailureOf(() => service.CaptureAllMonitorsAsync()));
        using (PermissionApprovals.Approve(PermissionCapability.ScreenCapture))
        {
            Assert.Equal(ScreenCaptureFailure.NothingToCapture, await FailureOf(() => service.CaptureAllMonitorsAsync()));
        }

        Assert.Equal(1, inner.Reached);
    }

    private sealed class Fixed(Assistant.Core.Settings.PermissionSettings permissions) : ISettingsService
    {
        public Task<Assistant.Core.Settings.AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Assistant.Core.Settings.AppSettings { Permissions = permissions });

        public Task SaveAsync(Assistant.Core.Settings.AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ---- the selection ----

    private sealed class Selection : ISelectionService, ICopySelectionService
    {
        public int Reached { get; private set; }

        public Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default)
        {
            Reached++;
            return Task.FromResult(SelectionResult.NoForegroundApp());
        }

        public Task<CopySelectionResult> CopySelectionAsync(CancellationToken cancellationToken = default)
        {
            Reached++;
            return Task.FromResult(CopySelectionResult.Of(CopySelectionStatus.NoForegroundApp, null));
        }
    }

    [Fact]
    public async Task TheSelectionIsNotReadWhileSelectedTextIsNotAllowedAndIsReadWhenItIs()
    {
        var inner = new Selection();

        var refused = await new PermissionCheckedSelectionService(inner, new Policy()).GetSelectionAsync();
        var read = await new PermissionCheckedSelectionService(inner, new Policy(PermissionCapability.SelectedText)).GetSelectionAsync();

        Assert.Equal(SelectionStatus.NotAllowed, refused.Status);
        Assert.Null(refused.Text);
        Assert.Equal(SelectionStatus.NoForegroundApp, read.Status);
        Assert.Equal(1, inner.Reached);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CopyIsNeverPressedWithoutBothPermissions(bool selectedText, bool byCopy)
    {
        var inner = new Selection();
        var allowed = new List<PermissionCapability>();
        if (selectedText)
        {
            allowed.Add(PermissionCapability.SelectedText);
        }

        if (byCopy)
        {
            allowed.Add(PermissionCapability.SelectedTextByCopy);
        }

        var result = await new PermissionCheckedCopySelectionService(inner, new Policy([.. allowed])).CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.NotAllowed, result.Status);
        Assert.Equal(ClipboardRestoreOutcome.NotNeeded, result.Restore);
        Assert.Equal(0, inner.Reached);
    }

    [Fact]
    public async Task CopyIsPressedWhenBothPermissionsAreAllowed()
    {
        var inner = new Selection();

        var result = await new PermissionCheckedCopySelectionService(inner, new Policy(PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy)).CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.NoForegroundApp, result.Status);
        Assert.Equal(1, inner.Reached);
    }
}
