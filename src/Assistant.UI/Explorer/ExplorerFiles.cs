using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Ipc;

namespace Assistant.UI.Explorer;

/// <summary>
/// What File Explorer's Ask Assistant brings to the conversation once its files have been checked: the pictures and the
/// documents to attach, by full path, and, for the files that cannot be attached, a notice that names each of them
/// (PROJECT_SPEC §4.4: unsupported or unreadable files are reported per file, and the rest are used). Every document is attached,
/// and a question reads them all (PROJECT_SPEC §5.5, several files).
/// </summary>
/// <param name="Pictures">The pictures to attach, in the order they came.</param>
/// <param name="Documents">The documents to attach, in the order they came.</param>
/// <param name="Notice">What was left out and why, or <see langword="null"/> when nothing was.</param>
internal sealed record ExplorerFiles(IReadOnlyList<string> Pictures, IReadOnlyList<string> Documents, string? Notice)
{
    // How many names a sentence lists before it counts the rest.
    private const int NamesListed = 3;

    /// <summary>Whether anything can be attached.</summary>
    public bool HasFiles => Pictures.Count > 0 || Documents.Count > 0;

    /// <summary>Sorts checked files into what is attached and what is reported.</summary>
    public static ExplorerFiles From(IReadOnlyList<InvokedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        List<string> pictures = [];
        List<string> documents = [];
        List<string> missing = [];
        List<string> folders = [];
        List<string> unsupported = [];
        var overLimit = 0;
        foreach (var file in files)
        {
            switch (file.Problem)
            {
                case InvokedFileProblem.None when ImageFileTypes.IsImageExtension(Path.GetExtension(file.Path)):
                    pictures.Add(file.Path);
                    break;
                case InvokedFileProblem.None:
                    documents.Add(file.Path);
                    break;
                case InvokedFileProblem.NotFound or InvokedFileProblem.NotAPath:
                    missing.Add(file.Name);
                    break;
                case InvokedFileProblem.Folder:
                    folders.Add(file.Name);
                    break;
                case InvokedFileProblem.NotSupported:
                    unsupported.Add(file.Name);
                    break;
                case InvokedFileProblem.OverLimit:
                    overLimit++;
                    break;
            }
        }

        List<string> sentences = [];
        if (missing.Count > 0)
        {
            sentences.Add($"{List(missing)} couldn't be found.");
        }

        if (folders.Count > 0)
        {
            sentences.Add($"{List(folders)} {(folders.Count == 1 ? "is a folder" : "are folders")}, and only files can be attached.");
        }

        if (unsupported.Count > 0)
        {
            sentences.Add($"The Assistant can't read {List(unsupported)}.");
        }

        if (overLimit > 0)
        {
            sentences.Add(OverLimit(overLimit));
        }

        return new ExplorerFiles(pictures, documents, sentences.Count > 0 ? string.Join(" ", sentences) : null);
    }

    /// <summary>
    /// Joins two batches that came before the window could open for the first: the pictures and the documents of both, each once,
    /// and what each had to say, once. Documents beyond the most one question takes are named as left out, as in one batch.
    /// </summary>
    public static ExplorerFiles Combine(ExplorerFiles earlier, ExplorerFiles later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        var pictures = earlier.Pictures.Concat(later.Pictures).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var documents = earlier.Documents.Concat(later.Documents).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<string> notices = [];
        if (earlier.Notice is not null)
        {
            notices.Add(earlier.Notice);
        }

        if (later.Notice is not null && !notices.Contains(later.Notice))
        {
            notices.Add(later.Notice);
        }

        var over = documents.Length - InvocationProtocol.MaxFiles;
        if (over > 0)
        {
            documents = documents[..InvocationProtocol.MaxFiles];
            notices.Add(OverLimit(over));
        }

        return new ExplorerFiles(pictures, documents, notices.Count > 0 ? string.Join(" ", notices) : null);
    }

    private static string OverLimit(int count) =>
        $"Only {InvocationProtocol.MaxFiles} files can be sent at once, so {count} more {(count == 1 ? "was" : "were")} left out.";

    // "a.pdf", "a.pdf and b.pdf", "a.pdf, b.pdf and c.pdf", "a.pdf, b.pdf, c.pdf and 2 other files".
    private static string List(IReadOnlyList<string> names)
    {
        if (names.Count == 1)
        {
            return names[0];
        }

        if (names.Count <= NamesListed)
        {
            return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        }

        var others = names.Count - NamesListed;
        return $"{string.Join(", ", names.Take(NamesListed))} and {others} other {(others == 1 ? "file" : "files")}";
    }
}
