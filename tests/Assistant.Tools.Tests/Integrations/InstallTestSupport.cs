using Assistant.Tools.Integrations;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>What the package registries say, as the test controls it, and what was asked.</summary>
internal sealed class FakePackageMetadata : IPackageMetadata
{
    public Dictionary<string, NpmFacts?> Npm { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, PyPiFacts?> PyPi { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public DiscoveryFailure? Fails { get; set; }

    public bool Hangs { get; set; }

    public async Task<NpmFacts?> GetNpmAsync(string name, string? version, CancellationToken cancellationToken)
    {
        lock (Asked)
        {
            Asked.Add("npm:" + name + "@" + (version ?? "latest"));
        }

        await Maybe(cancellationToken);
        return Npm.TryGetValue(name, out var facts) ? facts : null;
    }

    public async Task<PyPiFacts?> GetPyPiAsync(string name, string? version, CancellationToken cancellationToken)
    {
        lock (Asked)
        {
            Asked.Add("pypi:" + name + "@" + (version ?? "latest"));
        }

        await Maybe(cancellationToken);
        return PyPi.TryGetValue(name, out var facts) ? facts : null;
    }

    private async Task Maybe(CancellationToken cancellationToken)
    {
        if (Hangs)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Fails is { } failure)
        {
            throw new DiscoveryException(failure);
        }
    }
}

/// <summary>Candidates and package facts as the review sees them.</summary>
internal static class Reviewable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public static readonly string Sha512Hex = new string('b', 128);

    public static readonly string Sha256Hex = new string('a', 64);

    /// <summary>A server the maker publishes on npm and lists in the official registry: a tool for creating tasks is listed.</summary>
    public static IntegrationCandidate NpmVendor(
        string package = "@doist/todoist-mcp",
        string? version = "13.4.0",
        CandidateTrust trust = CandidateTrust.VerifiedVendor,
        IReadOnlyList<string>? tools = null,
        CapabilityEvidence evidence = CapabilityEvidence.ToolListed,
        IReadOnlyList<string>? secrets = null,
        DateTimeOffset? activity = null,
        string? license = "MIT",
        string? description = "The official Todoist MCP server") => new()
    {
        Name = "io.github.Doist/todoist-mcp",
        SourceUrl = "https://github.com/Doist/todoist-mcp",
        RepositoryUrl = "https://github.com/Doist/todoist-mcp",
        Publisher = "Doist",
        Trust = trust,
        License = license,
        LastActivity = activity ?? Now.AddDays(-3),
        Description = description,
        Packages = [new CandidatePackage(CandidateInstallMethod.Npm, package, version)],
        Runtime = CandidateRuntime.NodeJs,
        RequiredSecrets = secrets ?? ["TODOIST_API_KEY"],
        ToolNames = tools ?? ["add-tasks", "find-tasks"],
        Version = version,
        CommitSha = new string('c', 40),
        Evidence = evidence,
        FoundIn = ["mcp-registry", "github"],
    };

    public static NpmFacts NpmFactsFor(
        string name = "@doist/todoist-mcp",
        string version = "13.4.0",
        bool scripts = false,
        int dependencies = 3,
        string? node = ">=18",
        string? deprecated = null,
        bool noHash = false,
        bool noBin = false,
        string? tarball = null,
        string? license = "MIT") => new()
    {
        Name = name,
        Version = version,
        TarballUrl = tarball ?? $"https://registry.npmjs.org/{name}/-/{name[(name.IndexOf('/') + 1)..]}-{version}.tgz",
        Hash = noHash ? null : new ContentHash("sha512", Sha512Hex),
        UnpackedSize = 31_899,
        Bin = noBin ? new Dictionary<string, string>() : new Dictionary<string, string> { ["todoist-mcp"] = "dist/index.js" },
        HasInstallScripts = scripts,
        DependencyCount = dependencies,
        NodeConstraint = node,
        License = license,
        RepositoryUrl = "https://github.com/Doist/todoist-mcp",
        GitHead = new string('d', 40),
        Deprecated = deprecated,
        Description = "The official Todoist MCP server",
    };

    public static PyPiFacts PyPiFactsFor(
        string name = "todoist-mcp",
        string version = "0.4.2",
        bool wheel = true,
        bool yanked = false,
        bool sourceOnly = false,
        string? requiresPython = ">=3.10") => new()
    {
        Name = name,
        Version = version,
        Wheel = wheel ? new PyPiWheel("todoist_mcp-0.4.2-py3-none-any.whl", "https://files.pythonhosted.org/packages/aa/bb/todoist_mcp-0.4.2-py3-none-any.whl", Sha256Hex, 6_821) : null,
        HasSourceOnly = sourceOnly,
        Yanked = yanked,
        RequiresPython = requiresPython,
        DependencyCount = 4,
        License = "MIT",
        RepositoryUrl = "https://github.com/example/todoist-mcp",
        Summary = "An MCP server for Todoist",
        UploadedAt = Now.AddDays(-20),
    };

    public static IntegrationCandidate PyPi(string package = "todoist-mcp", string? version = "0.4.2", IReadOnlyList<string>? secrets = null) => new()
    {
        Name = package,
        SourceUrl = "https://pypi.org/project/" + package + "/",
        Publisher = "example",
        Trust = CandidateTrust.Community,
        License = "MIT",
        LastActivity = Now.AddDays(-20),
        Description = "An MCP server for Todoist",
        Packages = [new CandidatePackage(CandidateInstallMethod.PyPi, package, version)],
        Runtime = CandidateRuntime.Python,
        RequiredSecrets = secrets ?? [],
        Version = version,
        Evidence = CapabilityEvidence.Described,
        FoundIn = ["pypi"],
    };

    public static IntegrationCandidate Remote(string url = "https://ai.todoist.net/mcp", IReadOnlyList<string>? secrets = null, CandidateTrust trust = CandidateTrust.VerifiedVendor) => new()
    {
        Name = "com.todoist/mcp",
        SourceUrl = "https://registry.modelcontextprotocol.io/v0/servers?search=todoist",
        Publisher = "todoist.com",
        Trust = trust,
        LastActivity = Now.AddDays(-12),
        Description = "Todoist, hosted. Create and find tasks",
        Packages = [new CandidatePackage(CandidateInstallMethod.Remote, url, null)],
        RemoteUrl = url,
        Runtime = CandidateRuntime.None,
        RequiredSecrets = secrets ?? [],
        Evidence = CapabilityEvidence.Described,
        FoundIn = ["mcp-registry"],
    };

    public static IntegrationCandidate Bundle(string url = "https://github.com/example/todoist-mcp/releases/download/v1.0.0/todoist.mcpb", string? version = "1.0.0", string? sha = null) => new()
    {
        Name = "example/todoist-mcp",
        SourceUrl = "https://github.com/example/todoist-mcp",
        RepositoryUrl = "https://github.com/example/todoist-mcp",
        Publisher = "example",
        Trust = CandidateTrust.Community,
        License = "Apache-2.0",
        LastActivity = Now.AddDays(-40),
        Description = "Todoist MCP server bundle",
        Packages = [new CandidatePackage(CandidateInstallMethod.Bundle, url, version, sha ?? Sha256Hex)],
        Version = version,
        ToolNames = ["create_task"],
        Evidence = CapabilityEvidence.ToolListed,
        FoundIn = ["mcp-registry"],
    };
}
