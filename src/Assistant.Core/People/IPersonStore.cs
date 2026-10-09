namespace Assistant.Core.People;

/// <summary>
/// Where the people the user has told the Assistant about are kept (PROJECT_SPEC §3.5, §4.8, step 112). The app's store is the local database;
/// nothing here goes to a connected app, so what "my brother" means stays on this PC. A store keeps people tidy (<see cref="PersonRules"/>) and
/// never logs a name, an alias or an address.
/// </summary>
public interface IPersonStore
{
    /// <summary>Raised after a person was saved or removed, so a list that shows people can read them again.</summary>
    event EventHandler? Changed;

    /// <summary>Every person, ordered by name.</summary>
    /// <exception cref="PersonStoreException">The people could not be read.</exception>
    Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The person <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    /// <exception cref="PersonStoreException">The people could not be read.</exception>
    Task<Person?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps <paramref name="person"/>, tidied by <see cref="PersonRules.Normalize"/>: adds them, or replaces the name, aliases, relationships and
    /// addresses of the one with the same id. Their time of creation stays the first one. Returns the person as it was kept.
    /// </summary>
    /// <exception cref="PersonValidationException">The person cannot be kept as written, or there are too many people.</exception>
    /// <exception cref="PersonStoreException">The person could not be saved.</exception>
    Task<Person> SaveAsync(Person person, CancellationToken cancellationToken = default);

    /// <summary>Removes the person <paramref name="id"/>, with their aliases, relationships and addresses. Returns whether they were there.</summary>
    /// <exception cref="PersonStoreException">The person could not be removed.</exception>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
