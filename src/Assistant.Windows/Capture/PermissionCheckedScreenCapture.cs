using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Windows.Placement;

namespace Assistant.Windows.Capture;

/// <summary>
/// The screen capture service behind the Screen Capture permission (PROJECT_SPEC §4.6, §4.9, step 119): every capture asks <see cref="IPermissionPolicy"/> first, in the
/// service, so a capture made by any caller (the Visual Intelligence overlay, a tool, a quick action) is refused while Screen Capture is off or this build cannot do it, and while it
/// is set to ask every time and nobody asked about this use (the code that asks opens an approval for the work that follows, <c>PermissionApprovals</c>). It adds nothing else:
/// the monitors are listed without a check, since listing them shows nothing of what is on screen.
/// </summary>
/// <param name="inner">The service that captures.</param>
/// <param name="permissions">Says whether Screen Capture may be used now.</param>
public sealed class PermissionCheckedScreenCapture(IScreenCapture inner, IPermissionPolicy permissions) : IScreenCapture
{
    /// <inheritdoc/>
    public IReadOnlyList<CaptureMonitor> GetMonitors() => inner.GetMonitors();

    /// <inheritdoc/>
    public async Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        return await inner.CaptureMonitorAsync(monitor, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        return await inner.CaptureAllMonitorsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        return await inner.CaptureWindowAsync(window, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        return await inner.CaptureRegionAsync(region, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireAsync(CancellationToken cancellationToken)
    {
        if (!(await permissions.CheckAsync(PermissionCapability.ScreenCapture, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            throw new ScreenCaptureException(ScreenCaptureFailure.NotAllowed);
        }
    }
}
