namespace Assistant.Core.People;

/// <summary>
/// A <see cref="IPersonStore"/> that keeps people in memory and forgets them when the app ends: for tests and for trying the Assistant before anything is
/// saved. It applies the same rules as the database store, so a test that passes with it passes with the real one.
/// </summary>
/// <param name="clock">What says now; the system clock when omitted.</param>
public sealed class InMemoryPersonStore(TimeProvider? clock = null) : IPersonStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Person> _people = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Person>>(
                [.. _people.Values.OrderBy(person => person.DisplayName, PersonRules.NameOrder).ThenBy(person => person.Id)]);
        }
    }

    /// <inheritdoc/>
    public Task<Person?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_people.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc/>
    public Task<Person> SaveAsync(Person person, CancellationToken cancellationToken = default)
    {
        var tidy = PersonRules.Normalize(person);
        Person kept;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var exists = _people.TryGetValue(tidy.Id, out var before);
            if (!exists && _people.Count >= PersonRules.MaxPeople)
            {
                throw new PersonValidationException($"The Assistant keeps at most {PersonRules.MaxPeople} people.");
            }

            kept = tidy with { CreatedAt = exists ? before!.CreatedAt : now, UpdatedAt = now };
            _people[kept.Id] = kept;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(kept);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool removed;
        lock (_gate)
        {
            removed = _people.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(removed);
    }
}
