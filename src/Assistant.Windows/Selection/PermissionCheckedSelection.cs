using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Windows.Selection;

/// <summary>
/// Reading another app's selection behind the Selected Text permission (PROJECT_SPEC §4.5, §4.9, step 119): the service asks <see cref="IPermissionPolicy"/> itself, so no caller can
/// read a selection while Selected Text is off, or set to ask every time and not asked about. Refused, it answers <see cref="SelectionStatus.NotAllowed"/> and has asked the
/// application for nothing.
/// </summary>
/// <param name="inner">The service that reads the selection.</param>
/// <param name="permissions">Says whether Selected Text may be used now.</param>
public sealed class PermissionCheckedSelectionService(ISelectionService inner, IPermissionPolicy permissions) : ISelectionService
{
    /// <inheritdoc/>
    public async Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default) =>
        (await permissions.CheckAsync(PermissionCapability.SelectedText, cancellationToken).ConfigureAwait(false)).IsAllowed
            ? await inner.GetSelectionAsync(cancellationToken).ConfigureAwait(false)
            : SelectionResult.NotAllowed();
}

/// <summary>
/// Pressing Copy in another app behind both permissions it needs (PROJECT_SPEC §4.5, §4.9, step 119): Selected Text and Selected Text by Copy. Refused, nothing is sent to the application and
/// the clipboard is not touched; it answers <see cref="CopySelectionStatus.NotAllowed"/>.
/// </summary>
/// <param name="inner">The service that presses Copy.</param>
/// <param name="permissions">Says whether both may be used now.</param>
public sealed class PermissionCheckedCopySelectionService(ICopySelectionService inner, IPermissionPolicy permissions) : ICopySelectionService
{
    /// <inheritdoc/>
    public async Task<CopySelectionResult> CopySelectionAsync(CancellationToken cancellationToken = default)
    {
        if (!(await permissions.CheckAsync(PermissionCapability.SelectedText, cancellationToken).ConfigureAwait(false)).IsAllowed
            || !(await permissions.CheckAsync(PermissionCapability.SelectedTextByCopy, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return CopySelectionResult.Of(CopySelectionStatus.NotAllowed, null);
        }

        return await inner.CopySelectionAsync(cancellationToken).ConfigureAwait(false);
    }
}
