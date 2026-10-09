using System.Globalization;
using System.Text.Json;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>What a package installer needs to put an integration on this PC.</summary>
/// <param name="Candidate">What is installed, as reviewed and approved.</param>
/// <param name="VersionDirectory">The folder this version of the integration goes into: it exists and is empty.</param>
/// <param name="DownloadDirectory">A folder for what is downloaded and is not kept; it is deleted afterwards.</param>
/// <param name="Runtime">The runtime the package runs on, set up already, or <see langword="null"/>.</param>
/// <param name="Progress">Told how the installation is going.</param>
internal sealed record PackageInstallContext(
    InstallCandidate Candidate, string VersionDirectory, string DownloadDirectory, ManagedRuntime? Runtime, IProgress<InstallProgress>? Progress);

/// <summary>What a package installer made: how to start the integration, and the files that must be there for it to start.</summary>
/// <param name="Transport">How the integration is started or reached.</param>
/// <param name="RequiredFiles">Paths (full) that must exist for it to start, for telling later that what was installed is still whole.</param>
/// <param name="Runtime">The runtime it is started with, when it needs one the Assistant sets up.</param>
internal sealed record PreparedInstall(IntegrationTransport Transport, IReadOnlyList<string> RequiredFiles, ManagedRuntime? Runtime = null);

/// <summary>Puts one kind of package on this PC (PROJECT_SPEC §4.8, step 108).</summary>
internal interface IPackageInstaller
{
    /// <summary>The kind it installs.</summary>
    InstallSourceKind Kind { get; }

    /// <summary>The runtime the package needs the Assistant to set up, or <see langword="null"/>.</summary>
    RuntimeKind? RuntimeNeeded(InstallCandidate candidate);

    /// <summary>
    /// Downloads what the review pinned, checks it against the checksum, unpacks it into <see cref="PackageInstallContext.VersionDirectory"/> and works out how to
    /// start it. Nothing it downloaded is run, except the package manager that fetches dependencies with scripts turned off.
    /// </summary>
    /// <exception cref="InstallException">It could not be done.</exception>
    /// <exception cref="OperationCanceledException">The installation was cancelled.</exception>
    Task<PreparedInstall> PrepareAsync(PackageInstallContext context, CancellationToken cancellationToken);
}

/// <summary>Shared pieces of the installers.</summary>
internal static class InstallerSupport
{
    /// <summary>The most a package or a bundle download may be.</summary>
    public const long MaxPackageBytes = 200L * 1024 * 1024;

    /// <summary>The longest a dependency install may take.</summary>
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(6);

    /// <summary>Reports a download's progress in words.</summary>
    public static IProgress<long> DownloadProgress(IProgress<InstallProgress>? progress, string what, long? expectedBytes)
    {
        var last = -1;
        return new Progress<long>(received =>
        {
            var whole = (int)(received / 1_048_576);
            if (whole == last)
            {
                return;
            }

            last = whole;
            var of = expectedBytes is > 0 ? $" of {Math.Max(1, (int)Math.Round(expectedBytes.Value / 1_048_576.0)).ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            progress?.Report(new InstallProgress(
                InstallStep.Downloading,
                $"Downloading {what} ({whole.ToString(CultureInfo.InvariantCulture)}{of} MB)",
                expectedBytes is > 0 ? Math.Clamp((double)received / expectedBytes.Value, 0, 1) : null));
        });
    }

    /// <summary>A path inside <paramref name="root"/> made from a relative path a package gave, or <see langword="null"/> when it goes outside it.</summary>
    public static string? Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Any(char.IsControl) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>
/// Installs an npm package (PROJECT_SPEC §4.8, step 108): the package's file is downloaded by the Assistant and checked against the checksum the review pinned,
/// and then npm, which the managed Node.js brings with it, installs that file and fetches what it depends on, with install scripts turned off, from the
/// npm registry only, with a configuration of its own and none of the user's. The program to start is the one the package names as its <c>bin</c>, run
/// by the managed Node.js; it is started only after the installation is finished and recorded.
/// </summary>
internal sealed class NpmPackageInstaller(IPackageDownloader downloader, IProcessRunner runner) : IPackageInstaller
{
    /// <inheritdoc/>
    public InstallSourceKind Kind => InstallSourceKind.Npm;

    /// <inheritdoc/>
    public RuntimeKind? RuntimeNeeded(InstallCandidate candidate) => RuntimeKind.NodeJs;

    /// <inheritdoc/>
    public async Task<PreparedInstall> PrepareAsync(PackageInstallContext context, CancellationToken cancellationToken)
    {
        var source = context.Candidate.Source;
        var node = context.Runtime ?? throw new InstallException(InstallFailure.RuntimeUnavailable, "Node.js is not set up.");
        var npmCli = Path.Combine(node.Directory, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli))
        {
            throw new InstallException(InstallFailure.RuntimeUnavailable, "The Node.js I set up has no package manager, so I cannot fetch what the package depends on.");
        }

        // The package's own file, checked against the checksum the review pinned.
        var tarball = Path.Combine(context.DownloadDirectory, "package.tgz");
        await downloader.DownloadAsync(
            new Uri(source.DownloadUrl!), tarball, source.Hash!, InstallerSupport.MaxPackageBytes,
            InstallerSupport.DownloadProgress(context.Progress, "the integration", null), cancellationToken).ConfigureAwait(false);

        // npm installs that file and what it depends on into a folder of its own.
        var app = Path.Combine(context.VersionDirectory, "app");
        Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(Path.Combine(app, "package.json"), "{\"name\":\"assistant-integration\",\"version\":\"1.0.0\",\"private\":true}", cancellationToken).ConfigureAwait(false);
        // npm reads a user's and a global configuration file, and refuses to load one file as both: each is an empty file of its own.
        var userConfig = Path.Combine(context.DownloadDirectory, "npmrc-user");
        var globalConfig = Path.Combine(context.DownloadDirectory, "npmrc-global");
        await File.WriteAllTextAsync(userConfig, string.Empty, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(globalConfig, string.Empty, cancellationToken).ConfigureAwait(false);
        context.Progress?.Report(new InstallProgress(InstallStep.InstallingDependencies, "Fetching what it depends on"));
        var result = await runner.RunAsync(
            new ProcessSpec(
                node.ExecutablePath,
                [
                    npmCli, "install", tarball, "--ignore-scripts", "--no-audit", "--no-fund", "--no-update-notifier", "--no-package-lock", "--loglevel=error",
                    "--prefix", app, "--registry", "https://registry.npmjs.org/", "--userconfig", userConfig, "--globalconfig", globalConfig,
                    "--cache", Path.Combine(context.DownloadDirectory, "npm-cache"),
                ],
                app,
                new Dictionary<string, string>(),
                InstallerSupport.SetupTimeout),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InstallException(InstallFailure.SetupFailed, result.TimedOut ? "Fetching what it depends on took too long." : "I could not fetch what the package depends on.");
        }

        // The program the package says it provides, inside its own folder.
        var packageDirectory = InstallerSupport.Inside(Path.Combine(app, "node_modules"), source.Identifier)
            ?? throw new InstallException(InstallFailure.SetupFailed, "The package was not installed where I expected.");
        var entry = ReadEntry(packageDirectory, source);
        return new PreparedInstall(
            new IntegrationTransport
            {
                Kind = McpTransportKind.Stdio,
                Command = node.ExecutablePath,
                Arguments = [entry],
                WorkingDirectory = packageDirectory,
            },
            [node.ExecutablePath, entry],
            node);
    }

    // The file the package's bin names (the one the review recorded when it is there), which must be a script inside the package.
    private static string ReadEntry(string packageDirectory, InstallSource source)
    {
        var manifest = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(manifest))
        {
            throw new InstallException(InstallFailure.EntryPointMissing, "The package has no description of itself.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var bin = new Dictionary<string, string>(StringComparer.Ordinal);
        if (WebJson.Member(root, "bin") is { } element)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } single)
            {
                var name = source.Identifier.Contains('/', StringComparison.Ordinal) ? source.Identifier[(source.Identifier.IndexOf('/', StringComparison.Ordinal) + 1)..] : source.Identifier;
                bin[name] = single;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject().Take(16))
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } path)
                    {
                        bin[property.Name] = path;
                    }
                }
            }
        }

        if (bin.Count == 0)
        {
            throw new InstallException(InstallFailure.EntryPointMissing, "The package provides no program to start.");
        }

        var chosen = source.EntryName is { } wanted && bin.ContainsKey(wanted) ? wanted : bin.Keys.OrderBy(name => name, StringComparer.Ordinal).First();
        var file = InstallerSupport.Inside(packageDirectory, bin[chosen])
            ?? throw new InstallException(InstallFailure.EntryPointMissing, "The program it names is not inside the package.");
        var extension = Path.GetExtension(file);
        if (!File.Exists(file) || extension.Length > 0 && !extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallException(InstallFailure.EntryPointMissing, "The program it names is not one I can start.");
        }

        return file;
    }
}

/// <summary>
/// Installs a PyPI package (PROJECT_SPEC §4.8, step 108): the wheel is downloaded by the Assistant and checked against the checksum the review pinned, and then
/// pip, in a virtual environment the managed Python makes for this integration alone, installs that file and fetches what it depends on, wheels only
/// (so nothing is built, and no package's setup code runs), from PyPI only, with a configuration of its own. The program to start is the console
/// script the package declares.
/// </summary>
internal sealed class PyPiPackageInstaller(IPackageDownloader downloader, IProcessRunner runner) : IPackageInstaller
{
    /// <inheritdoc/>
    public InstallSourceKind Kind => InstallSourceKind.PyPi;

    /// <inheritdoc/>
    public RuntimeKind? RuntimeNeeded(InstallCandidate candidate) => RuntimeKind.Python;

    /// <inheritdoc/>
    public async Task<PreparedInstall> PrepareAsync(PackageInstallContext context, CancellationToken cancellationToken)
    {
        var source = context.Candidate.Source;
        var python = context.Runtime ?? throw new InstallException(InstallFailure.RuntimeUnavailable, "Python is not set up.");
        var wheelName = Path.GetFileName(new Uri(source.DownloadUrl!).AbsolutePath);
        if (!WheelTags.IsInstallable(wheelName) || wheelName.Any(character => character is '/' or '\\' or ':'))
        {
            throw new InstallException(InstallFailure.NotAllowed, "The package's file is not a wheel I can install.");
        }

        var wheel = Path.Combine(context.DownloadDirectory, wheelName);
        await downloader.DownloadAsync(
            new Uri(source.DownloadUrl!), wheel, source.Hash!, InstallerSupport.MaxPackageBytes,
            InstallerSupport.DownloadProgress(context.Progress, "the integration", source.SizeBytes), cancellationToken).ConfigureAwait(false);

        // An environment of its own for this integration, so that what it depends on cannot clash with another's.
        var venv = Path.Combine(context.VersionDirectory, "venv");
        context.Progress?.Report(new InstallProgress(InstallStep.InstallingDependencies, "Setting up its Python environment"));
        var make = await runner.RunAsync(
            new ProcessSpec(python.ExecutablePath, ["-m", "venv", venv], context.VersionDirectory, new Dictionary<string, string>(), InstallerSupport.SetupTimeout),
            cancellationToken).ConfigureAwait(false);
        var environmentPython = Path.Combine(venv, "Scripts", "python.exe");
        if (!make.Succeeded || !File.Exists(environmentPython))
        {
            throw new InstallException(InstallFailure.SetupFailed, "I could not make a Python environment for it.");
        }

        context.Progress?.Report(new InstallProgress(InstallStep.InstallingDependencies, "Fetching what it depends on"));
        var install = await runner.RunAsync(
            new ProcessSpec(
                environmentPython,
                [
                    "-m", "pip", "--isolated", "install", "--no-input", "--disable-pip-version-check", "--only-binary=:all:", "--no-cache-dir",
                    "--index-url", "https://pypi.org/simple", wheel,
                ],
                context.VersionDirectory,
                new Dictionary<string, string>(),
                InstallerSupport.SetupTimeout),
            cancellationToken).ConfigureAwait(false);
        if (!install.Succeeded)
        {
            throw new InstallException(InstallFailure.SetupFailed, install.TimedOut ? "Fetching what it depends on took too long." : "I could not fetch what the package depends on.");
        }

        var script = ConsoleScript(venv, source) ?? throw new InstallException(InstallFailure.EntryPointMissing, "The package provides no program to start.");
        return new PreparedInstall(
            new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = script, Arguments = [], WorkingDirectory = context.VersionDirectory },
            [script],
            python);
    }

    // The console script the package declares, as a program in the environment's Scripts folder.
    private static string? ConsoleScript(string venv, InstallSource source)
    {
        var site = Path.Combine(venv, "Lib", "site-packages");
        if (!Directory.Exists(site))
        {
            return null;
        }

        var normalized = source.Identifier.Replace('-', '_').Replace('.', '_').ToLowerInvariant();
        foreach (var info in Directory.EnumerateDirectories(site, "*.dist-info"))
        {
            var folder = Path.GetFileName(info);
            var named = folder[..^".dist-info".Length];
            var dash = named.LastIndexOf('-');
            if (dash <= 0)
            {
                continue;
            }

            var stem = named[..dash].Replace('-', '_').Replace('.', '_').ToLowerInvariant();
            var entryPoints = Path.Combine(info, "entry_points.txt");
            if (stem != normalized || !File.Exists(entryPoints))
            {
                continue;
            }

            var scripts = new List<string>();
            var inConsoleScripts = false;
            foreach (var raw in File.ReadLines(entryPoints))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inConsoleScripts = line == "[console_scripts]";
                    continue;
                }

                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (inConsoleScripts && equals > 0 && line[..equals].Trim() is { Length: > 0 and <= 80 } name && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
                {
                    scripts.Add(name);
                }
            }

            var chosen = scripts.FirstOrDefault(name => name == source.EntryName)
                ?? scripts.FirstOrDefault(name => name.Replace('-', '_').Equals(normalized, StringComparison.OrdinalIgnoreCase))
                ?? scripts.FirstOrDefault();
            var program = chosen is null ? null : Path.Combine(venv, "Scripts", chosen + ".exe");
            return program is not null && File.Exists(program) ? program : null;
        }

        return null;
    }
}

/// <summary>
/// Installs an MCP bundle (<c>.mcpb</c>; PROJECT_SPEC §4.8, step 108): a zip with a <c>manifest.json</c> and the program. It is downloaded and checked against the
/// checksum the review pinned, unpacked with the archive rules of <see cref="ArchiveExtractor"/>, and its manifest read as data: only a server that is a
/// program inside the bundle (<c>binary</c>) or a script run by the managed Node.js (<c>node</c>) is started, from files inside the bundle, with the arguments
/// the manifest lists; a manifest that asks for settings the user would have to give is not installed, since this version has nowhere to take them.
/// </summary>
internal sealed class BundlePackageInstaller(IPackageDownloader downloader, IManagedRuntimes runtimes) : IPackageInstaller
{
    private const string DirectoryMarker = "${__dirname}";

    /// <inheritdoc/>
    public InstallSourceKind Kind => InstallSourceKind.Bundle;

    /// <inheritdoc/>
    public RuntimeKind? RuntimeNeeded(InstallCandidate candidate) => candidate.Runtime == CandidateRuntime.NodeJs ? RuntimeKind.NodeJs : null;

    /// <inheritdoc/>
    public async Task<PreparedInstall> PrepareAsync(PackageInstallContext context, CancellationToken cancellationToken)
    {
        var source = context.Candidate.Source;
        var file = Path.Combine(context.DownloadDirectory, "bundle.mcpb");
        await downloader.DownloadAsync(
            new Uri(source.DownloadUrl!), file, source.Hash!, InstallerSupport.MaxPackageBytes,
            InstallerSupport.DownloadProgress(context.Progress, "the integration", source.SizeBytes), cancellationToken).ConfigureAwait(false);

        context.Progress?.Report(new InstallProgress(InstallStep.Unpacking, "Unpacking the integration"));
        var bundle = Path.Combine(context.VersionDirectory, "bundle");
        await Task.Run(() => ArchiveExtractor.Extract(file, ArchiveKind.Zip, bundle, string.Empty, ExtractionLimits.Default, cancellationToken), cancellationToken).ConfigureAwait(false);

        var manifestPath = Path.Combine(bundle, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InstallException(InstallFailure.EntryPointMissing, "The bundle has no manifest, so I do not know how to start it.");
        }

        JsonDocument manifest;
        try
        {
            manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException exception)
        {
            throw new InstallException(InstallFailure.EntryPointMissing, "The bundle's manifest cannot be read.", exception);
        }

        using (manifest)
        {
            var root = manifest.RootElement;
            if (WebJson.Text(root, "version") is { } listed && source.Version is { } expected && !string.Equals(listed, expected, StringComparison.Ordinal))
            {
                throw new InstallException(InstallFailure.NotAllowed, "The bundle is not the version that was reviewed.");
            }

            var server = WebJson.Member(root, "server") ?? default;
            var config = WebJson.Member(server, "mcp_config") ?? default;
            var type = WebJson.Text(server, "type");
            var arguments = new List<string>();
            foreach (var argument in WebJson.Items(config, "args").Take(32))
            {
                if (argument.ValueKind != JsonValueKind.String || argument.GetString() is not { } text || text.Contains("${user_config", StringComparison.Ordinal))
                {
                    throw new InstallException(InstallFailure.Unsupported, "The bundle asks for settings I cannot take yet.");
                }

                arguments.Add(ResolveInside(bundle, text));
            }

            if (WebJson.Member(config, "env") is { ValueKind: JsonValueKind.Object } env && env.EnumerateObject().Any())
            {
                throw new InstallException(InstallFailure.Unsupported, "The bundle asks for settings I cannot take yet.");
            }

            switch (type)
            {
                case "binary":
                    var entry = WebJson.Text(server, "entry_point");
                    var command = WebJson.Text(config, "command") ?? entry;
                    var program = command is null ? null : InstallerSupport.Inside(bundle, command.Replace(DirectoryMarker + "/", string.Empty, StringComparison.Ordinal).Replace(DirectoryMarker + "\\", string.Empty, StringComparison.Ordinal));
                    if (program is null || !File.Exists(program) || !Path.GetExtension(program).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InstallException(InstallFailure.EntryPointMissing, "The program the bundle names is not inside it, or is not a program I can start.");
                    }

                    return new PreparedInstall(
                        new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = program, Arguments = arguments, WorkingDirectory = bundle }, [program], null);
                case "node":
                    // A bundle says what it runs on only once it is open; what it needs is set up now, as part of what the user approved.
                    var node = context.Runtime ?? await runtimes.EnsureAsync(RuntimeKind.NodeJs, context.Progress, cancellationToken).ConfigureAwait(false);
                    if (arguments.Count == 0 || !File.Exists(arguments[0]))
                    {
                        throw new InstallException(InstallFailure.EntryPointMissing, "The script the bundle names is not inside it.");
                    }

                    return new PreparedInstall(
                        new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = node.ExecutablePath, Arguments = arguments, WorkingDirectory = bundle },
                        [node.ExecutablePath, arguments[0]],
                        node);
                default:
                    throw new InstallException(InstallFailure.Unsupported, "The bundle is a kind of server I cannot start yet.");
            }
        }
    }

    // An argument as a path inside the bundle where it names the bundle's folder; any other argument is used as it is.
    private static string ResolveInside(string bundle, string argument)
    {
        if (!argument.Contains(DirectoryMarker, StringComparison.Ordinal))
        {
            return argument;
        }

        var relative = argument.Replace(DirectoryMarker, string.Empty, StringComparison.Ordinal).TrimStart('/', '\\');
        return InstallerSupport.Inside(bundle, relative) ?? throw new InstallException(InstallFailure.EntryPointMissing, "The bundle names a file outside itself.");
    }
}
