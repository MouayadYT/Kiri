using System.Text;
using Assistant.Tools.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 106: what the finder found is kept for a while, in memory and in one small file, and what is read back is checked like anything from the web.</summary>
public sealed class DiscoveryCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assistant-discovery-" + Guid.NewGuid().ToString("N"));

    public DiscoveryCacheTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that cannot be removed is left to the system.
        }
    }

    private string FilePath => Path.Combine(_folder, "cache", "integration-discovery.json");

    private static IntegrationDiscoveryResult Result(params string[] names) => new()
    {
        Status = DiscoveryStatus.Found,
        SearchedAt = Now,
        SourcesAnswered = ["github"],
        Candidates = [.. names.Select(name => Candidates.Make(name, CandidateTrust.VerifiedVendor, tools: ["add-tasks"]) with
        {
            License = "MIT",
            CommitSha = new string('b', 40),
            Packages = [new CandidatePackage(CandidateInstallMethod.Npm, "@x/" + name.Split('/')[^1], "1.0.0")],
        })],
    };

    [Fact]
    public void AnAnswerIsKeptUntilItExpires()
    {
        var cache = new DiscoveryCache(null, 10);
        cache.Put("a|b", Result("x/todoist-mcp"), Now.AddHours(1));

        Assert.NotNull(cache.TryGet("a|b", Now));
        Assert.NotNull(cache.TryGet("a|b", Now.AddMinutes(59)));
        Assert.Null(cache.TryGet("a|b", Now.AddHours(1)));
        Assert.Null(cache.TryGet("a|b", Now.AddHours(2)));
        Assert.Null(cache.TryGet("other", Now));
    }

    [Fact]
    public void WhatIsKeptComesBackWhole()
    {
        var cache = new DiscoveryCache(null, 10);
        var result = Result("x/todoist-mcp");
        cache.Put("k", result, Now.AddHours(1));

        var back = cache.TryGet("k", Now)!;

        Assert.Equal(DiscoveryStatus.Found, back.Status);
        Assert.Equal(result.Candidates, back.Candidates);
        Assert.False(back.FromCache);
    }

    [Fact]
    public void AnAnswerSurvivesARestart()
    {
        new DiscoveryCache(FilePath, 10).Put("todoist|create:task", Result("x/todoist-mcp"), Now.AddHours(24));

        var back = new DiscoveryCache(FilePath, 10).TryGet("todoist|create:task", Now)!;

        var candidate = Assert.Single(back.Candidates);
        Assert.Equal("x/todoist-mcp", candidate.Name);
        Assert.Equal(CandidateTrust.VerifiedVendor, candidate.Trust);
        Assert.Equal(["add-tasks"], candidate.ToolNames);
        Assert.Equal(new CandidatePackage(CandidateInstallMethod.Npm, "@x/todoist-mcp", "1.0.0"), Assert.Single(candidate.Packages));
        Assert.Equal(["github"], back.SourcesAnswered);
    }

    [Fact]
    public void TheFileHoldsNoRequestAndNoPersonalText()
    {
        new DiscoveryCache(FilePath, 10).Put("todoist|create:task", Result("x/todoist-mcp"), Now.AddHours(24));

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"schemaVersion\":1", text, StringComparison.Ordinal);
        Assert.Contains("todoist|create:task", text, StringComparison.Ordinal);
        Assert.DoesNotContain("milk", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void ThereAreAtMostSoManyAnswersAndTheOneThatExpiresFirstGoesFirst()
    {
        var cache = new DiscoveryCache(FilePath, 3);
        cache.Put("a", Result("x/a-todoist"), Now.AddHours(5));
        cache.Put("b", Result("x/b-todoist"), Now.AddHours(1));
        cache.Put("c", Result("x/c-todoist"), Now.AddHours(9));
        cache.Put("d", Result("x/d-todoist"), Now.AddHours(7));

        Assert.Null(cache.TryGet("b", Now));
        Assert.All(new[] { "a", "c", "d" }, key => Assert.NotNull(cache.TryGet(key, Now)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"schemaVersion\":99,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"key\":\"k\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"key\":\"k\",\"expiresAt\":\"2099-01-01T00:00:00Z\",\"result\":{\"candidates\":[{\"nope\":1}]}}]}")]
    public void AFileThatIsNotWhatWasWrittenIsAnEmptyCache(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        var cache = new DiscoveryCache(FilePath, 10);

        Assert.Null(cache.TryGet("k", Now));
        cache.Put("k", Result("x/todoist-mcp"), Now.AddHours(1));
        Assert.NotNull(new DiscoveryCache(FilePath, 10).TryGet("k", Now));
    }

    [Fact]
    public void AFileThatIsTooBigIsNotRead()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, new string(' ', 3 * 1024 * 1024));

        Assert.Null(new DiscoveryCache(FilePath, 10).TryGet("k", Now));
    }

    [Fact]
    public void WhatIsReadBackIsCheckedLikeAnythingFromTheWeb()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var edited = """
            {"schemaVersion":1,"entries":[{"key":"k","expiresAt":"2099-01-01T00:00:00Z","result":{"status":"Found","searchedAt":"2026-10-02T12:00:00Z","candidates":[
              {"name":"good/todoist","sourceUrl":"https://github.com/good/todoist","description":"fine","trust":"VerifiedVendor"},
              {"name":"bad/todoist","sourceUrl":"http://github.com/bad/todoist","description":"insecure address"},
              {"name":"worse/todoist","sourceUrl":"javascript:alert(1)"},
              {"name":"sneaky/todoist","sourceUrl":"https://github.com/sneaky/todoist","description":"line one\nline two","remoteUrl":"http://insecure.example","commitSha":"not-a-sha","trust":99}
            ]}}]}
            """;
        File.WriteAllText(FilePath, edited, new UTF8Encoding(false));

        var back = new DiscoveryCache(FilePath, 10).TryGet("k", Now)!;

        Assert.Equal(["good/todoist", "sneaky/todoist"], back.Candidates.Select(candidate => candidate.Name));
        var sneaky = back.Candidates[1];
        Assert.DoesNotContain('\n', sneaky.Description!);
        Assert.Null(sneaky.RemoteUrl);
        Assert.Null(sneaky.CommitSha);
        Assert.Equal(CandidateTrust.Unknown, sneaky.Trust);
    }

    [Fact]
    public void ACacheThatCannotBeWrittenStillKeepsWhatItHoldsInMemory()
    {
        // The "file" is a folder, so it cannot be replaced.
        Directory.CreateDirectory(FilePath);
        var cache = new DiscoveryCache(FilePath, 10);

        cache.Put("k", Result("x/todoist-mcp"), Now.AddHours(1));

        Assert.NotNull(cache.TryGet("k", Now));
    }
}
