namespace Assistant.Tools.Integrations;

/// <summary>How the installed integrations changed.</summary>
public enum IntegrationChangeKind
{
    /// <summary>One was installed.</summary>
    Added = 0,

    /// <summary>One was changed (enabled, disabled, its health or sign-in recorded, its address changed).</summary>
    Updated = 1,

    /// <summary>One was removed.</summary>
    Removed = 2,
}

/// <summary>An installed integration was added, changed or removed.</summary>
/// <param name="IntegrationId">Which one.</param>
/// <param name="Kind">What happened to it.</param>
public sealed class IntegrationsChangedEventArgs(string integrationId, IntegrationChangeKind kind) : EventArgs
{
    /// <summary>Which integration.</summary>
    public string IntegrationId { get; } = integrationId;

    /// <summary>What happened to it.</summary>
    public IntegrationChangeKind Kind { get; } = kind;
}

/// <summary>
/// The connected apps the user has installed (PROJECT_SPEC §4.8, step 104), kept so that they can be reconnected to later. It holds a record for
/// each (<see cref="InstalledIntegration"/>): the information needed to reconnect and nothing more, never a secret. Every change is
/// checked against <see cref="IntegrationRules"/> and written before it takes effect, so what is held in memory is what is on disk. It is
/// changed only by the code that installs and configures integrations: no tool and nothing the model says reaches it.
/// </summary>
public interface IInstalledIntegrationRegistry
{
    /// <summary>Raised after an integration was added, changed or removed, on the thread that changed it and after the change was written.</summary>
    event EventHandler<IntegrationsChangedEventArgs>? Changed;

    /// <summary>Every installed integration, in the order they were installed.</summary>
    Task<IReadOnlyList<InstalledIntegration>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The integration with <paramref name="id"/>, or <see langword="null"/> when none is installed under it.</summary>
    Task<InstalledIntegration?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Installs <paramref name="integration"/>.</summary>
    /// <returns>The integration as installed.</returns>
    /// <exception cref="IntegrationException">It breaks a rule (<see cref="IntegrationFailure.Invalid"/>), its id is taken, or the list could not be written.</exception>
    Task<InstalledIntegration> AddAsync(InstalledIntegration integration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the integration with <paramref name="id"/> to what <paramref name="change"/> makes of it. The id cannot be changed. A change that
    /// leaves the record as it was writes nothing and raises nothing.
    /// </summary>
    /// <returns>The integration after the change.</returns>
    /// <exception cref="IntegrationException">None has that id, the result breaks a rule, or the list could not be written.</exception>
    Task<InstalledIntegration> UpdateAsync(string id, Func<InstalledIntegration, InstalledIntegration> change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the integration with <paramref name="id"/> and deletes the secrets it named from the secret store.
    /// </summary>
    /// <returns><see langword="true"/> when there was one.</returns>
    /// <exception cref="IntegrationException">The list could not be written.</exception>
    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default);
}
