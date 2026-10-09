using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>A downloader the test controls: what each address serves. It checks the hash and the size limit as the real one does.</summary>
internal sealed class FakePackageDownloader : IPackageDownloader
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Uri> Downloads { get; } = [];

    public Func<Uri, Task>? Before { get; set; }

    public async Task<DownloadedFile> DownloadAsync(Uri uri, string destination, ContentHash expected, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        lock (Downloads)
        {
            Downloads.Add(uri);
        }

        if (Before is not null)
        {
            await Before(uri);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Files.TryGetValue(uri.AbsoluteUri, out var bytes))
        {
            throw new InstallException(InstallFailure.DownloadFailed, "It is not there to download any more.");
        }

        if (bytes.Length > maxBytes)
        {
            throw new InstallException(InstallFailure.DownloadFailed, "The download is larger than I allow.");
        }

        var actual = expected.Compute(bytes);
        if (!string.Equals(actual, expected.Hex, StringComparison.Ordinal))
        {
            throw new InstallException(InstallFailure.HashMismatch, "What I downloaded is not what was reviewed, so I deleted it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
        progress?.Report(bytes.Length);
        return new DownloadedFile(destination, bytes.Length);
    }

    public int Count => Downloads.Count;
}

/// <summary>A runner of setup programs the test controls: what each does to the folders, and what it was asked to run.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Runs { get; } = [];

    public Func<ProcessSpec, ProcessResult>? Behaviour { get; set; }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Runs)
        {
            Runs.Add(spec);
        }

        return Task.FromResult(Behaviour?.Invoke(spec) ?? new ProcessResult(0, false, false));
    }
}

/// <summary>Runtimes the test controls: set up by writing the few files that matter into a folder.</summary>
internal sealed class FakeRuntimes(string root) : IManagedRuntimes
{
    private readonly HashSet<RuntimeKind> _ready = [];

    public List<RuntimeKind> Ensured { get; } = [];

    public IReadOnlySet<(RuntimeKind, string)>? LastInUse { get; private set; }

    public Exception? EnsureFails { get; set; }

    public RuntimeRelease? ReleaseFor(RuntimeKind kind) => RuntimeCatalog.For(kind, System.Runtime.InteropServices.Architecture.X64);

    public ManagedRuntime? Find(RuntimeKind kind) => _ready.Contains(kind) ? Describe(kind) : null;

    public void MakeReady(RuntimeKind kind)
    {
        Describe(kind);
        _ready.Add(kind);
    }

    public Task<ManagedRuntime> EnsureAsync(RuntimeKind kind, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        Ensured.Add(kind);
        if (EnsureFails is not null)
        {
            throw EnsureFails;
        }

        progress?.Report(new InstallProgress(InstallStep.DownloadingRuntime, "Downloading a runtime"));
        MakeReady(kind);
        return Task.FromResult(Describe(kind));
    }

    public int RemoveUnused(IReadOnlySet<(RuntimeKind Kind, string Version)> inUse)
    {
        LastInUse = inUse;
        return 0;
    }

    private ManagedRuntime Describe(RuntimeKind kind)
    {
        var release = ReleaseFor(kind)!;
        var directory = Path.Combine(root, kind == RuntimeKind.NodeJs ? "node" : "python", release.Version);
        Directory.CreateDirectory(Path.Combine(directory, "node_modules", "npm", "bin"));
        var executable = Path.Combine(directory, release.Executable);
        if (!File.Exists(executable))
        {
            File.WriteAllText(executable, "not a program");
            File.WriteAllText(Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js"), "// npm");
        }

        return new ManagedRuntime(kind, release.Version, release.DisplayName, directory, executable);
    }
}

/// <summary>What the connection manager is told to let go of.</summary>
internal sealed class FakeConnections : IIntegrationConnections
{
    public List<string> Forgotten { get; } = [];

    public Func<string, Task>? OnForget { get; set; }

    public List<string> Reconnected { get; } = [];

    public ConnectionCheck ReconnectResult { get; set; } = new(true, false, null, 2, true);

    public ConnectionCheck HealthResult { get; set; } = new(true, false, null, 0, false);

    public async Task ForgetAsync(string integrationId, CancellationToken cancellationToken)
    {
        Forgotten.Add(integrationId);
        if (OnForget is not null)
        {
            await OnForget(integrationId);
        }
    }

    public Task<ConnectionCheck> ReconnectAsync(string integrationId, CancellationToken cancellationToken)
    {
        Reconnected.Add(integrationId);
        return Task.FromResult(ReconnectResult);
    }

    public Task<ConnectionCheck> CheckHealthAsync(string integrationId, CancellationToken cancellationToken) => Task.FromResult(HealthResult);
}

/// <summary>Archives made in memory, as the internet might serve them.</summary>
internal static class TestArchives
{
    public static byte[] Zip(params (string Name, string Content)[] entries) => Zip(entries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content))).ToArray());

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                target.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>A zip whose one entry is marked, in its Unix attributes, as the kind of thing given (0xA000 is a symbolic link).</summary>
    public static byte[] ZipWithUnixType(string name, int unixType)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = (unixType | 0x1ED) << 16;
            using var target = entry.Open();
            target.Write("target"u8);
        }

        return stream.ToArray();
    }

    public static byte[] TarGz(params (string Name, string Content, TarEntryType Type)[] entries)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var (name, content, type) in entries)
            {
                TarEntry entry = type switch
                {
                    TarEntryType.SymbolicLink => new PaxTarEntry(type, name) { LinkName = content },
                    TarEntryType.HardLink => new PaxTarEntry(type, name) { LinkName = content },
                    TarEntryType.Directory => new PaxTarEntry(type, name),
                    _ => new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) },
                };
                writer.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    public static ContentHash Sha256(byte[] bytes) => new("sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    public static ContentHash Sha512(byte[] bytes) => new("sha512", Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant());
}

/// <summary>An MCP bundle and the install candidate that stands for it, as the review would have pinned it.</summary>
internal static class SampleBundle
{
    public const string Url = "https://github.com/example/notes-mcp/releases/download/v1.0.0/notes.mcpb";

    public static string Manifest(string version = "1.0.0", string type = "binary", string command = "${__dirname}/server/notes.exe", string args = "[]", string env = "{}") =>
        "{\"manifest_version\":\"0.2\",\"name\":\"notes\",\"version\":\"" + version + "\",\"server\":{\"type\":\"" + type
        + "\",\"entry_point\":\"server/notes.exe\",\"mcp_config\":{\"command\":\"" + command + "\",\"args\":" + args + ",\"env\":" + env + "}}}";

    public static byte[] Bytes(string version = "1.0.0", string? manifest = null, string type = "binary", string args = "[]", string command = "${__dirname}/server/notes.exe") =>
        TestArchives.Zip(("manifest.json", manifest ?? Manifest(version, type, command, args)), ("server/notes.exe", "MZ pretend program"), ("server/index.js", "// script"));

    public static InstallCandidate Candidate(
        byte[] bundle,
        string version = "1.0.0",
        string id = "notes",
        string app = "Notes",
        string? url = null,
        IReadOnlyList<string>? secrets = null,
        IntegrationCapability? capability = null,
        CandidateRuntime runtime = CandidateRuntime.None)
    {
        var source = new InstallSource
        {
            Kind = InstallSourceKind.Bundle,
            Identifier = url ?? Url,
            Version = version,
            DownloadUrl = url ?? Url,
            Hash = TestArchives.Sha256(bundle),
            SizeBytes = bundle.Length,
        };
        return new InstallCandidate
        {
            Id = id,
            AppName = app,
            Name = "example/notes-mcp",
            Source = source,
            Trust = CandidateTrust.Community,
            Publisher = "example",
            SourceUrl = "https://github.com/example/notes-mcp",
            RepositoryUrl = "https://github.com/example/notes-mcp",
            License = "MIT",
            LicenseStatus = LicenseStatus.Open,
            Runtime = runtime,
            RequiredSecrets = secrets ?? [],
            Authentication = secrets is { Count: > 0 } ? IntegrationAuthKind.EnvironmentSecret : IntegrationAuthKind.None,
            Capability = capability ?? new IntegrationCapability(CapabilityAction.Create, "note"),
            Evidence = CapabilityEvidence.ToolListed,
            FoundIn = ["mcp-registry"],
            ReviewedAt = Reviewable.Now,
            Fingerprint = InstallCandidate.FingerprintOf(id, app, source),
        };
    }
}

/// <summary>The installer with everything around it, in a folder of its own.</summary>
internal sealed class InstallFixture : IDisposable
{
    public InstallFixture(
        bool localOnly = false,
        bool webPermission = true,
        Func<InstalledIntegration, StubMcpClient>? client = null,
        IManagedRuntimes? runtimes = null)
    {
        Folder = new TempFolder();
        Paths = new AppPaths(Folder.Path);
        Paths.EnsureDirectoriesExist();
        Layout = new IntegrationLayout(Paths);
        Store = new MemoryIntegrationStore();
        Registry = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance, Secrets);
        Downloader = new FakePackageDownloader();
        Runner = new FakeProcessRunner();
        Runtimes = runtimes as FakeRuntimes ?? new FakeRuntimes(Paths.RuntimesDirectory);
        Clients = new StubClientFactory(client ?? (_ => Server()));
        Settings = TestSettings.LocalOnly(localOnly);
        Clock = new ManualTimeProvider(Reviewable.Now);
        Connections = new FakeConnections();
        Installer = new IntegrationInstaller(
            Layout, Registry, runtimes ?? Runtimes, Downloader, Clients, Settings, new FakePermissions(webPermission), Clock,
            NullLogger<IntegrationInstaller>.Instance, null, Runner, Connections);
    }

    public TempFolder Folder { get; }

    public AppPaths Paths { get; }

    public IntegrationLayout Layout { get; }

    public MemoryIntegrationStore Store { get; }

    public FakeSecretStore Secrets { get; } = new();

    public InstalledIntegrationRegistry Registry { get; }

    public FakePackageDownloader Downloader { get; }

    public FakeProcessRunner Runner { get; }

    public FakeRuntimes Runtimes { get; }

    public StubClientFactory Clients { get; }

    public FixedSettings Settings { get; }

    public ManualTimeProvider Clock { get; }

    public FakeConnections Connections { get; }

    public IntegrationInstaller Installer { get; }

    /// <summary>A server that offers a tool for creating a note and one for listing notes.</summary>
    public static StubMcpClient Server(params string[] tools)
    {
        var client = new StubMcpClient { TransportKind = Assistant.Tools.Mcp.McpTransportKind.Stdio };
        foreach (var name in tools.Length == 0 ? ["create_note", "list_notes"] : tools)
        {
            client.Tools.Add(Sample.Tool(name));
        }

        return client;
    }

    /// <summary>Puts a bundle where the downloader serves it and returns the candidate for it.</summary>
    public InstallCandidate Serve(byte[]? bundle = null, string version = "1.0.0", string id = "notes", IReadOnlyList<string>? secrets = null, IntegrationCapability? capability = null)
    {
        bundle ??= SampleBundle.Bytes(version);
        var url = $"https://github.com/example/notes-mcp/releases/download/v{version}/notes.mcpb";
        Downloader.Files[url] = bundle;
        return SampleBundle.Candidate(bundle, version, id, url: url, secrets: secrets, capability: capability);
    }

    /// <summary>Every file and folder under the app's integrations and runtimes folders, relative, for seeing that nothing was left behind.</summary>
    public IReadOnlyList<string> Leftovers()
    {
        var root = Paths.IntegrationsDirectory;
        return !Directory.Exists(root)
            ? []
            : [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path))];
    }

    public void Dispose() => Folder.Dispose();
}
