using System.Collections.Concurrent;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Search.Index;
using Microsoft.Extensions.Logging;

namespace Assistant.Search.Tests;

/// <summary>An index that answers with the rows it is given and remembers every query it was asked.</summary>
internal sealed class FakeIndex : ISearchIndexClient
{
    private readonly ConcurrentQueue<string> _queries = new();

    /// <summary>What it answers with for a query, by whether the query is for folders.</summary>
    public Func<string, IReadOnlyList<object?[]>> Answer { get; set; } = _ => [];

    /// <summary>What it throws instead of answering, when set.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<string> Queries => [.. _queries];

    public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queries.Enqueue(sql);
        return Failure is null ? Task.FromResult(Answer(sql)) : Task.FromException<IReadOnlyList<object?[]>>(Failure);
    }
}

/// <summary>What Windows can read of file types, as the test says; a type it is not told about is unknown.</summary>
internal sealed class FakeContentTypes(params (string Extension, ContentTypeSupport Support)[] types) : IContentTypeCatalog
{
    public List<string> Asked { get; } = [];

    public ContentTypeSupport Lookup(string extension)
    {
        Asked.Add(extension);
        foreach (var (known, support) in types)
        {
            if (string.Equals(known, extension, StringComparison.OrdinalIgnoreCase))
            {
                return support;
            }
        }

        return ContentTypeSupport.Unknown;
    }
}

/// <summary>Settings that are whatever the test says.</summary>
internal sealed class FakeSettings(params string[] excludedFolders) : ISettingsService
{
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppSettings { Privacy = new PrivacySettings { ExcludedFolders = excludedFolders } });

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Keeps every log entry with its structured values, so a test sees everything the code hands to its logger.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<string> _entries = new();

    public string AllText => string.Join(Environment.NewLine, _entries);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue($"{logLevel} {eventId.Id} {formatter(state, exception)} {exception}");
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var (name, value) in values)
            {
                _entries.Enqueue($"  {name} = {value}");
            }
        }
    }
}

/// <summary>Builds rows in the order of <see cref="SearchColumns"/>.</summary>
internal static class Rows
{
    public static object?[] File(
        string path,
        DateTime? modified = null,
        object? size = null,
        string? extension = null,
        long attributes = 32) =>
        Row(path, extension: extension ?? Path.GetExtension(path), size: size ?? 1234UL, modified: modified, attributes: attributes, isFolder: false);

    public static object?[] Folder(string path, long attributes = 16) =>
        Row(path, extension: null, size: 0UL, attributes: attributes, isFolder: true);

    public static object?[] Row(
        string path,
        string? extension,
        object? size = null,
        DateTime? modified = null,
        long attributes = 32,
        bool isFolder = false,
        int length = 15)
    {
        var row = new object?[16];
        row[SearchColumns.Path] = path;
        row[SearchColumns.Name] = Path.GetFileName(path);
        row[SearchColumns.Extension] = extension;
        row[SearchColumns.Size] = size;
        row[SearchColumns.Modified] = modified ?? new DateTime(2026, 5, 1, 12, 0, 0);
        row[SearchColumns.Created] = new DateTime(2026, 4, 1, 8, 30, 0);
        row[SearchColumns.Attributes] = attributes;
        row[SearchColumns.IsFolder] = isFolder;
        return row[..length];
    }
}
