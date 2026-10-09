namespace Assistant.Tools.Integrations;

/// <summary>What was read from where the installed integrations are kept.</summary>
/// <param name="Integrations">The integrations that were read and break no rule (<see cref="IntegrationRules"/>), each id once.</param>
/// <param name="Skipped">How many entries were left out because they could not be read, broke a rule or repeated an id.</param>
/// <param name="Unreadable">Whether the whole file could not be read (it was not JSON, or too large); the registry then starts empty.</param>
public sealed record IntegrationStoreContents(IReadOnlyList<InstalledIntegration> Integrations, int Skipped = 0, bool Unreadable = false)
{
    /// <summary>Nothing is installed.</summary>
    public static IntegrationStoreContents Empty { get; } = new([]);
}

/// <summary>
/// Where the installed integrations are kept between runs (PROJECT_SPEC §3.5, step 104): the registry reads them once and writes the whole
/// list back whenever it changes. What is kept is only what is needed to reconnect, and no secret.
/// </summary>
public interface IInstalledIntegrationStore
{
    /// <summary>Reads what is kept. A store with nothing in it gives <see cref="IntegrationStoreContents.Empty"/>.</summary>
    /// <exception cref="IntegrationException">What is kept could not be read for a reason other than its content (the file is locked).</exception>
    Task<IntegrationStoreContents> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces what is kept with <paramref name="integrations"/>, all or nothing.</summary>
    /// <exception cref="IntegrationException">It could not be written; what was kept before is left as it was.</exception>
    Task SaveAsync(IReadOnlyList<InstalledIntegration> integrations, CancellationToken cancellationToken = default);
}
