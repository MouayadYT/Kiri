using Assistant.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>
/// The app's <see cref="IInstalledIntegrationRegistry"/>: the list is read from the <see cref="IInstalledIntegrationStore"/> the first time it is
/// asked for and held in memory, and every change is written back before it is applied, so a change that cannot be written is not made.
/// Changes are made one at a time. It logs counts only, and the id of an integration (a slug), never its name, address, program or secrets.
/// </summary>
public sealed partial class InstalledIntegrationRegistry : IInstalledIntegrationRegistry
{
    private readonly IInstalledIntegrationStore _store;
    private readonly ISecretStore? _secrets;
    private readonly ILogger<InstalledIntegrationRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<InstalledIntegration>? _items;

    /// <summary>Creates the registry over <paramref name="store"/>.</summary>
    /// <param name="store">Where the list is kept.</param>
    /// <param name="logger">Where counts are logged.</param>
    /// <param name="secrets">Where the secrets the integrations name are kept; without it a removed integration's secrets are not deleted.</param>
    public InstalledIntegrationRegistry(
        IInstalledIntegrationStore store, ILogger<InstalledIntegrationRegistry> logger, ISecretStore? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _secrets = secrets;
    }

    /// <inheritdoc/>
    public event EventHandler<IntegrationsChangedEventArgs>? Changed;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<InstalledIntegration>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. await LoadedAsync(cancellationToken).ConfigureAwait(false)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<InstalledIntegration?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadedAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<InstalledIntegration> AddAsync(InstalledIntegration integration, CancellationToken cancellationToken = default)
    {
        Check(integration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await LoadedAsync(cancellationToken).ConfigureAwait(false);
            if (items.Any(item => item.Id == integration.Id))
            {
                throw new IntegrationException(IntegrationFailure.Duplicate);
            }

            List<InstalledIntegration> next = [.. items, integration];
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _items = next;
        }
        finally
        {
            _gate.Release();
        }

        LogChanged(_logger, integration.Id, IntegrationChangeKind.Added);
        Raise(integration.Id, IntegrationChangeKind.Added);
        return integration;
    }

    /// <inheritdoc/>
    public async Task<InstalledIntegration> UpdateAsync(
        string id, Func<InstalledIntegration, InstalledIntegration> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        InstalledIntegration updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await LoadedAsync(cancellationToken).ConfigureAwait(false);
            var index = items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                throw new IntegrationException(IntegrationFailure.NotFound);
            }

            var current = items[index];
            updated = change(current);
            Check(updated);
            if (updated.Id != current.Id)
            {
                throw new IntegrationException(IntegrationFailure.Invalid, ["The id of an installed integration cannot be changed."]);
            }

            if (IntegrationJson.Equivalent(current, updated))
            {
                return current;
            }

            List<InstalledIntegration> next = [.. items];
            next[index] = updated;
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _items = next;
        }
        finally
        {
            _gate.Release();
        }

        LogChanged(_logger, id, IntegrationChangeKind.Updated);
        Raise(id, IntegrationChangeKind.Updated);
        return updated;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        InstalledIntegration removed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await LoadedAsync(cancellationToken).ConfigureAwait(false);
            var index = items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            removed = items[index];
            List<InstalledIntegration> next = [.. items];
            next.RemoveAt(index);
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _items = next;
        }
        finally
        {
            _gate.Release();
        }

        await DeleteSecretsAsync(removed, cancellationToken).ConfigureAwait(false);
        LogChanged(_logger, id, IntegrationChangeKind.Removed);
        Raise(id, IntegrationChangeKind.Removed);
        return true;
    }

    private static void Check(InstalledIntegration? integration)
    {
        if (IntegrationRules.Problems(integration) is { Count: > 0 } problems)
        {
            throw new IntegrationException(IntegrationFailure.Invalid, problems);
        }
    }

    // The list, read the first time it is needed. Called with the gate held.
    private async Task<List<InstalledIntegration>> LoadedAsync(CancellationToken cancellationToken)
    {
        if (_items is not null)
        {
            return _items;
        }

        var contents = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _items = [.. contents.Integrations];
        LogLoaded(_logger, _items.Count, contents.Skipped, contents.Unreadable);
        return _items;
    }

    // What a removed integration named is no longer needed, and a credential left behind would outlive what it was for.
    private async Task DeleteSecretsAsync(InstalledIntegration removed, CancellationToken cancellationToken)
    {
        if (_secrets is null)
        {
            return;
        }

        foreach (var binding in removed.Authentication.Secrets)
        {
            try
            {
                await _secrets.DeleteAsync(binding.SecretName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SecretStoreException or ArgumentException)
            {
                // The integration is gone either way; a secret that could not be deleted is not a reason to say otherwise.
            }
        }
    }

    private void Raise(string id, IntegrationChangeKind kind)
    {
        try
        {
            Changed?.Invoke(this, new IntegrationsChangedEventArgs(id, kind));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // What listens to a change cannot undo it.
        }
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Installed integrations read: {Count} installed, {Skipped} left out, unreadable {Unreadable}")]
    private static partial void LogLoaded(ILogger logger, int count, int skipped, bool unreadable);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Integration {IntegrationId} {Change}")]
    private static partial void LogChanged(ILogger logger, string integrationId, IntegrationChangeKind change);
}
