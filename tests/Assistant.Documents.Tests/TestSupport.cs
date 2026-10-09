using System.Text;
using Assistant.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Assistant.Documents.Tests;

/// <summary>A folder of its own for one test's files, removed afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assistant-documents-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, string content, Encoding? encoding = null)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    public string WriteBytes(string name, byte[] bytes)
    {
        var path = File(name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Keeps everything logged, as the privacy filter would see it: the formatted message and every state value.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = new StringBuilder(formatter(state, exception));
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var value in values)
            {
                text.Append('|').Append(value.Key).Append('=').Append(value.Value);
            }
        }

        if (exception is not null)
        {
            text.Append('|').Append(exception);
        }

        lock (_entries)
        {
            _entries.Add(text.ToString());
        }
    }
}

internal static class ResultAssertions
{
    public static string Flatten(DocumentReadResult result) => string.Join("\n", result.Segments.Select(s => s.Text));
}
