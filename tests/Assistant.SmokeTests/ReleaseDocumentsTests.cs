using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Assistant.SmokeTests.Support;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// The release documents say what the suite checks and where the code is, and a document that drifts is worse than none: these keep RELEASE_CHECKLIST.md
/// and the module map in DEVELOPMENT.md in step with the code. They read the files and the compiled test names, and run nothing. Both documents stay on the
/// developer's PC (the repository publishes no Markdown but its READMEs), so in a copy cloned from GitHub they are not there and these are skipped.
/// </summary>
public sealed class ReleaseDocumentsTests
{
    private static readonly string[] Areas =
    [
        "Startup and offline operation", "Hotkey", "Compact, floating and full window", "Local model: generate and cancel", "History persistence",
        "Windows Search", "Reading one document", "Multi-file handoff from File Explorer", "Screenshot Q&A", "Selected browser text",
        "Quick app and file search", "Tool confirmation", "Local Only enforcement", "Packaging", "Clean install and uninstall",
    ];

    [LocalDocumentFact("RELEASE_CHECKLIST.md")]
    public async Task EveryCheckOfTheSuiteIsInTheChecklist_AndEveryCheckTheChecklistNamesExists_AndItsCountIsRight()
    {
        var text = await File.ReadAllTextAsync(Repo.Combine("RELEASE_CHECKLIST.md"));

        var listed = Regex.Matches(text, @"`(?<name>\w+SmokeTests\.\w+)`").Select(match => match.Groups["name"].Value).ToArray();
        var existing = typeof(ReleaseDocumentsTests).Assembly.GetTypes()
            .Where(type => type.IsPublic && type.Name.EndsWith("SmokeTests", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToArray();

        Assert.Equal(listed.Length, listed.Distinct().Count());
        Assert.Empty(existing.Except(listed));
        Assert.Empty(listed.Except(existing));
        Assert.Contains($"**{existing.Length} smoke checks**", text, StringComparison.Ordinal);
    }

    [LocalDocumentFact("RELEASE_CHECKLIST.md")]
    public async Task TheChecklistNumbersTheFifteenAreasOfTheStepInOrder()
    {
        var text = await File.ReadAllTextAsync(Repo.Combine("RELEASE_CHECKLIST.md"));

        var rows = Regex.Matches(text, @"^\| (?<number>\d+) \| (?<area>[^|]+) \|", RegexOptions.Multiline)
            .Select(match => (Number: int.Parse(match.Groups["number"].Value), Area: match.Groups["area"].Value.Trim())).ToArray();

        Assert.Equal(Enumerable.Range(1, Areas.Length), rows.Select(row => row.Number));
        Assert.Equal(Areas, rows.Select(row => row.Area));
    }

    [LocalDocumentFact("DEVELOPMENT.md")]
    public async Task TheModuleMapNamesEveryProjectOfTheSolution_AndNoneThatIsNotThere()
    {
        var notes = await File.ReadAllTextAsync(Repo.Combine("DEVELOPMENT.md"));
        var map = notes[notes.IndexOf("## Module map", StringComparison.Ordinal)..];
        map = map[..map.IndexOf("\n## ", 5, StringComparison.Ordinal)];

        var mapped = Regex.Matches(map, @"^\| `(?<project>Assistant\.\w+)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups["project"].Value).ToArray();
        var projects = Directory.GetDirectories(Repo.Combine("src")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(projects, mapped.Order(StringComparer.Ordinal));
        foreach (var project in new[] { "tests", "samples", "packaging", "third_party" })
        {
            Assert.Contains($"`{project}", notes, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Repo.Combine(project)), project + " is described in DEVELOPMENT.md but is not there.");
        }
    }

    [Fact]
    public async Task TheReadmesPicturesAreAllThere_AndCarryNothingButThePicture()
    {
        var readme = await File.ReadAllTextAsync(Repo.Combine("README.md"));
        var pictures = Regex.Matches(readme, @"src=""(?<path>docs/images/[^""]+)""").Select(match => match.Groups["path"].Value).Distinct().ToArray();

        Assert.NotEmpty(pictures);
        foreach (var picture in pictures)
        {
            var path = Repo.Combine(picture.Split('/'));
            Assert.True(File.Exists(path), picture + " is in the README but not in the repository.");
            var bytes = await File.ReadAllBytesAsync(path);
            switch (Path.GetExtension(path))
            {
                case ".png":
                    // Only the chunks that draw it: no time, text, colour profile or name of the program that made it.
                    Assert.Empty(PngChunks(bytes).Except(["IHDR", "PLTE", "IDAT", "IEND", "tRNS"]));
                    break;
                case ".jpg":
                    // No EXIF, XMP or comment (APP1 and up, COM): only the JFIF header, tables and the picture.
                    Assert.DoesNotContain(JpegSegments(bytes), marker => marker is >= 0xE1 and <= 0xEF or 0xFE);
                    break;
                case ".svg":
                    var svg = await File.ReadAllTextAsync(path);
                    foreach (var word in new[] { "<metadata", "<title", "c2pa", "inkscape", "sodipodi" })
                    {
                        Assert.DoesNotContain(word, svg, StringComparison.OrdinalIgnoreCase);
                    }

                    break;
                default:
                    Assert.Fail(picture + " is not a PNG, JPEG or SVG.");
                    break;
            }
        }
    }

    private static IEnumerable<string> PngChunks(byte[] png)
    {
        for (var at = 8; at + 8 <= png.Length;)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            yield return System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            at += 12 + length;
        }
    }

    private static IEnumerable<int> JpegSegments(byte[] jpeg)
    {
        for (var at = 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA;)
        {
            yield return jpeg[at + 1];
            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        }
    }

    /// <summary>A check of a document that is kept only on the developer's PC: skipped, and saying why, where the document is not there.</summary>
    private sealed class LocalDocumentFactAttribute : FactAttribute
    {
        public LocalDocumentFactAttribute(string document)
        {
            if (!File.Exists(Repo.Combine(document)))
            {
                Skip = document + " is kept on the developer's PC and is not in this copy of the repository.";
            }
        }
    }
}
