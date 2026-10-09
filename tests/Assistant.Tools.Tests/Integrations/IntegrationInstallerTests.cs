using System.Text;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 108: installing an integration the user approved, in the Assistant's own folders, with nothing left behind when it fails.</summary>
public sealed class IntegrationInstallerTests : IDisposable
{
    private readonly InstallFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private InstallFixture Fixture => _fixture;

    private async Task<InstallOutcome> Install(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        await _fixture.Installer.InstallAsync(candidate, progress, cancellationToken);

    private void AssertNothingLeft()
    {
        Assert.Empty(_fixture.Leftovers());
        Assert.Empty(_fixture.Store.Saved);
    }

    // ---- a bundle: the sample (mock) integration ----

    [Fact]
    public async Task ABundleIsDownloadedCheckedUnpackedStartedOnceAndRecordedAsInstalled()
    {
        var candidate = _fixture.Serve();

        var outcome = await Install(candidate);

        Assert.Equal(InstallStatus.Installed, outcome.Status);
        Assert.True(outcome.IsInstalled);
        Assert.Equal(["create_note", "list_notes"], outcome.ToolNames);
        Assert.Contains("Settings > Integrations", outcome.Message, StringComparison.Ordinal);
        var integration = Assert.Single(_fixture.Store.Saved);
        Assert.Equal("notes", integration.Id);
        Assert.Equal("Notes", integration.Name);
        Assert.True(integration.Enabled);
        Assert.Equal("1.0.0", integration.InstalledVersion);
        Assert.Equal(McpTransportKind.Stdio, integration.Transport.Kind);
        var versionDirectory = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0");
        Assert.Equal(Path.Combine(versionDirectory, "bundle", "server", "notes.exe"), integration.Transport.Command);
        Assert.Equal(Path.Combine(versionDirectory, "bundle"), integration.Transport.WorkingDirectory);
        Assert.True(File.Exists(integration.Transport.Command));
        Assert.True(File.Exists(Path.Combine(versionDirectory, "install.json")));
        Assert.Equal(IntegrationSourceKind.OfficialRegistry, integration.Source.Kind);
        Assert.Equal(candidate.Source.Identifier, integration.Source.Origin);
        Assert.Equal(["create_note", "list_notes"], integration.Capabilities.ToolNames);
        Assert.Equal("2026-07-28", integration.Capabilities.ProtocolVersion);
        Assert.Equal(IntegrationAuthState.NotRequired, integration.Authentication.State);
        Assert.False(integration.Permissions.AllowSideEffects is false);
        Assert.Empty(integration.Permissions.ReadOnlyTools);
        Assert.False(integration.Permissions.TrustToolAnnotations);

        var managed = integration.Managed!;
        Assert.Equal(InstallSourceKind.Bundle, managed.Kind);
        Assert.Equal(candidate.Source.Hash!.ToString(), managed.Hash);
        Assert.Equal(candidate.Fingerprint, managed.Fingerprint);
        Assert.Equal(Reviewable.Now, managed.InstalledAt);
    }

    [Fact]
    public async Task NothingIsDownloadedOrRunUntilInstallIsCalledNotByPlanningNotByOffering()
    {
        var candidate = _fixture.Serve();

        await _fixture.Installer.PlanAsync(candidate);

        Assert.Equal(0, _fixture.Downloader.Count);
        Assert.Equal(0, _fixture.Clients.CreateCalls);
        Assert.Empty(_fixture.Runner.Runs);
        Assert.Empty(_fixture.Runtimes.Ensured);
        Assert.Empty(_fixture.Leftovers());
    }

    [Fact]
    public async Task TheDownloadedBundleIsDeletedAndOnlyTheUnpackedFilesStay()
    {
        await Install(_fixture.Serve());

        Assert.DoesNotContain(_fixture.Leftovers(), path => path.Contains(".mcpb", StringComparison.Ordinal) || path.StartsWith(".staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheServerIsStartedOnlyToCheckItAndThenLetGoOf()
    {
        await Install(_fixture.Serve());

        var client = Assert.Single(_fixture.Clients.Created);
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal(1, client.ListCalls);
        Assert.Equal(1, client.Disposed);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task ProgressSaysEachStepInWords()
    {
        var steps = new List<InstallProgress>();

        await Install(_fixture.Serve(), new Progress<InstallProgress>(steps.Add));
        await Task.Delay(100);

        var kinds = steps.Select(step => step.Step).Distinct().ToList();
        Assert.Contains(InstallStep.Preparing, kinds);
        Assert.Contains(InstallStep.Downloading, kinds);
        Assert.Contains(InstallStep.Unpacking, kinds);
        Assert.Contains(InstallStep.Checking, kinds);
        Assert.Contains(InstallStep.Finishing, kinds);
        Assert.All(steps, step => Assert.DoesNotContain("http", step.Message, StringComparison.OrdinalIgnoreCase));
    }

    // ---- reuse without downloading again ----

    [Fact]
    public async Task TheSameVersionThatWasDownloadedBeforeIsReusedAndNothingIsDownloadedAgain()
    {
        var candidate = _fixture.Serve();
        await Install(candidate);
        Assert.Equal(1, _fixture.Downloader.Count);

        // The record is gone (say the list could not be saved the first time) but the files are whole.
        await _fixture.Registry.RemoveAsync("notes");
        var plan = await _fixture.Installer.PlanAsync(candidate);
        var again = await Install(candidate);

        Assert.True(plan.IntegrationAlreadyDownloaded);
        Assert.Empty(plan.Downloads);
        Assert.True(again.IsInstalled);
        Assert.Equal(1, _fixture.Downloader.Count);
        Assert.Single(_fixture.Store.Saved);
    }

    [Fact]
    public async Task AFolderWithoutTheCompletionFileIsNotReusedAndIsReplaced()
    {
        var candidate = _fixture.Serve();
        var folder = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "half.txt"), "half");

        var outcome = await Install(candidate);

        Assert.True(outcome.IsInstalled);
        Assert.Equal(1, _fixture.Downloader.Count);
        Assert.False(File.Exists(Path.Combine(folder, "half.txt")));
    }

    [Fact]
    public async Task ACompletionFileForAnotherCandidateIsNotReused()
    {
        var candidate = _fixture.Serve();
        await Install(candidate);
        await _fixture.Registry.RemoveAsync("notes");
        var record = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0", "install.json");
        await File.WriteAllTextAsync(record, (await File.ReadAllTextAsync(record)).Replace(candidate.Fingerprint, new string('0', 64), StringComparison.Ordinal));

        var plan = await _fixture.Installer.PlanAsync(candidate);

        Assert.False(plan.IntegrationAlreadyDownloaded);
    }

    [Fact]
    public async Task ACompletionFileThatPointsOutsideTheAssistantsFoldersIsNotReused()
    {
        var candidate = _fixture.Serve();
        await Install(candidate);
        await _fixture.Registry.RemoveAsync("notes");
        var record = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0", "install.json");
        var original = await File.ReadAllTextAsync(record);
        var command = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0", "bundle", "server", "notes.exe");
        await File.WriteAllTextAsync(record, original.Replace(command.Replace("\\", "\\\\", StringComparison.Ordinal), "C:\\\\Windows\\\\System32\\\\calc.exe", StringComparison.Ordinal));

        var plan = await _fixture.Installer.PlanAsync(candidate);

        Assert.False(plan.IntegrationAlreadyDownloaded);
    }

    // ---- everything that goes wrong leaves nothing ----

    [Fact]
    public async Task ADownloadThatIsNotWhatWasReviewedFailsAndLeavesNothing()
    {
        var candidate = _fixture.Serve();
        _fixture.Downloader.Files[candidate.Source.DownloadUrl!] = SampleBundle.Bytes("1.0.0", type: "binary", args: "[\"--different\"]");

        var outcome = await Install(candidate);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Equal(InstallFailure.HashMismatch, outcome.Failure);
        AssertNothingLeft();
        Assert.Equal(0, _fixture.Clients.CreateCalls);
    }

    [Fact]
    public async Task ADownloadThatFailsLeavesNothing()
    {
        var candidate = _fixture.Serve();
        _fixture.Downloader.Files.Clear();

        var outcome = await Install(candidate);

        Assert.Equal(InstallFailure.DownloadFailed, outcome.Failure);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AnIntegrationThatDoesNotStartIsTakenAwayAgain()
    {
        using var broken = new InstallFixture(client: _ => new StubMcpClient { ConnectFailure = new McpException(McpFailure.LaunchFailed), TransportKind = McpTransportKind.Stdio });
        var candidate = broken.Serve();

        var outcome = await broken.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.DidNotStart, outcome.Failure);
        Assert.Contains("removed it again", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(broken.Leftovers());
        Assert.Empty(broken.Store.Saved);
    }

    [Fact]
    public async Task AnIntegrationThatOffersNoToolsIsTakenAway()
    {
        using var empty = new InstallFixture(client: _ => new StubMcpClient { TransportKind = McpTransportKind.Stdio });

        var outcome = await empty.Installer.InstallAsync(empty.Serve());

        Assert.Equal(InstallFailure.DidNotStart, outcome.Failure);
        Assert.Empty(empty.Leftovers());
    }

    [Fact]
    public async Task AnIntegrationWithNoToolForWhatWasAskedIsTakenAwayAndSaysSo()
    {
        using var unrelated = new InstallFixture(client: _ => InstallFixture.Server("get_weather", "get_time"));

        var outcome = await unrelated.Installer.InstallAsync(unrelated.Serve());

        Assert.Equal(InstallFailure.LacksCapability, outcome.Failure);
        Assert.Contains("create a note", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(unrelated.Leftovers());
        Assert.Empty(unrelated.Store.Saved);
    }

    [Fact]
    public async Task ATimeoutWhileCheckingIsAFailureToStartAndNotAHang()
    {
        using var slow = new InstallFixture(client: _ => new StubMcpClient { TransportKind = McpTransportKind.Stdio, BeforeConnect = () => Task.Delay(Timeout.Infinite, CancellationToken.None) });
        var installer = new IntegrationInstaller(
            slow.Layout, slow.Registry, slow.Runtimes, slow.Downloader, slow.Clients, slow.Settings, new FakePermissions(true), slow.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrationInstaller>.Instance, new IntegrationInstallerOptions { CheckTimeout = TimeSpan.FromMilliseconds(200) }, slow.Runner, null);

        var outcome = await installer.InstallAsync(slow.Serve());

        Assert.Equal(InstallFailure.DidNotStart, outcome.Failure);
        Assert.Empty(slow.Leftovers());
    }

    [Fact]
    public async Task CancellingWhileDownloadingLeavesNothingAndSaysCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        _fixture.Downloader.Before = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        var outcome = await Install(_fixture.Serve(), cancellationToken: cancellation.Token);

        Assert.Equal(InstallStatus.Cancelled, outcome.Status);
        Assert.Contains("Nothing was installed", outcome.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AListThatCannotBeSavedTakesTheFilesAwayAgain()
    {
        _fixture.Store.FailWrites = true;

        var outcome = await Install(_fixture.Serve());

        Assert.Equal(InstallFailure.DiskFailed, outcome.Failure);
        Assert.Empty(_fixture.Leftovers());
    }

    [Fact]
    public async Task AnArchiveThatWouldWriteOutsideItsFolderIsRefusedAndNothingIsLeft()
    {
        var evil = TestArchives.Zip(("manifest.json", SampleBundle.Manifest()), ("../../../evil.exe", "MZ"));
        var candidate = _fixture.Serve(evil);

        var outcome = await Install(candidate);

        Assert.Equal(InstallFailure.SetupFailed, outcome.Failure);
        AssertNothingLeft();
        Assert.False(File.Exists(Path.Combine(_fixture.Folder.Path, "evil.exe")));
    }

    [Theory]
    [InlineData("python", "[]", "${__dirname}/server/notes.exe", InstallFailure.Unsupported)]
    [InlineData("binary", "[\"${user_config.api_key}\"]", "${__dirname}/server/notes.exe", InstallFailure.Unsupported)]
    [InlineData("binary", "[]", "${__dirname}/server/missing.exe", InstallFailure.EntryPointMissing)]
    [InlineData("binary", "[]", "${__dirname}/../../outside.exe", InstallFailure.EntryPointMissing)]
    [InlineData("binary", "[]", "${__dirname}/server/index.js", InstallFailure.EntryPointMissing)]
    [InlineData("binary", "[\"${__dirname}/../../x\"]", "${__dirname}/server/notes.exe", InstallFailure.EntryPointMissing)]
    public async Task ABundleWhoseManifestAsksForSomethingUnsafeOrUnsupportedIsNotInstalled(string type, string args, string command, InstallFailure expected)
    {
        var bundle = SampleBundle.Bytes(type: type, args: args, command: command);
        var candidate = _fixture.Serve(bundle);

        var outcome = await Install(candidate);

        Assert.Equal(expected, outcome.Failure);
        AssertNothingLeft();
    }

    [Fact]
    public async Task ABundleThatIsNotTheVersionThatWasReviewedIsNotInstalled()
    {
        var candidate = _fixture.Serve(SampleBundle.Bytes("2.0.0"), version: "1.0.0");

        var outcome = await Install(candidate);

        Assert.Equal(InstallFailure.NotAllowed, outcome.Failure);
        AssertNothingLeft();
    }

    [Fact]
    public async Task ABundleWithNoManifestIsNotInstalled()
    {
        var candidate = _fixture.Serve(TestArchives.Zip(("server/notes.exe", "MZ")));

        var outcome = await Install(candidate);

        Assert.Equal(InstallFailure.EntryPointMissing, outcome.Failure);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AManifestThatSetsEnvironmentVariablesIsNotInstalledSinceThereIsNowhereToTakeSettings()
    {
        var bundle = TestArchives.Zip(("manifest.json", SampleBundle.Manifest(env: "{\"KEY\":\"${user_config.key}\"}")), ("server/notes.exe", "MZ"));

        var outcome = await Install(_fixture.Serve(bundle));

        Assert.Equal(InstallFailure.Unsupported, outcome.Failure);
        AssertNothingLeft();
    }

    // ---- what is refused before anything happens ----

    [Fact]
    public async Task ACandidateThatWasChangedAfterTheReviewIsNotInstalledAndNothingIsDownloaded()
    {
        var candidate = _fixture.Serve();
        var tampered = candidate with { Source = candidate.Source with { DownloadUrl = "https://github.com/other/other/releases/download/v1/other.mcpb" } };

        var outcome = await Install(tampered);

        Assert.Equal(InstallFailure.NotAllowed, outcome.Failure);
        Assert.Equal(0, _fixture.Downloader.Count);
    }

    [Fact]
    public async Task ACandidateWithNoChecksumOrAnInexactVersionIsNotInstalled()
    {
        var candidate = _fixture.Serve();
        var source = candidate.Source with { Hash = null };
        var noHash = candidate with { Source = source, Fingerprint = InstallCandidate.FingerprintOf(candidate.Id, candidate.AppName, source) };
        var range = candidate.Source with { Version = "^1.0.0" };
        var inexact = candidate with { Source = range, Fingerprint = InstallCandidate.FingerprintOf(candidate.Id, candidate.AppName, range) };

        Assert.Equal(InstallFailure.NotAllowed, (await Install(noHash)).Failure);
        Assert.Equal(InstallFailure.NotAllowed, (await Install(inexact)).Failure);
        Assert.Equal(0, _fixture.Downloader.Count);
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    public async Task ACandidateWhoseIdIsNotAValidIdIsNotInstalled(string id)
    {
        var candidate = _fixture.Serve(id: "notes");
        var bad = candidate with { Id = id, Fingerprint = InstallCandidate.FingerprintOf(id, candidate.AppName, candidate.Source) };

        Assert.Equal(InstallFailure.NotAllowed, (await Install(bad)).Failure);
    }

    [Fact]
    public async Task AnIntegrationThatIsInstalledAlreadyIsNotInstalledOver()
    {
        var candidate = _fixture.Serve();
        await Install(candidate);

        var outcome = await Install(candidate);

        Assert.Equal(InstallFailure.AlreadyInstalled, outcome.Failure);
        Assert.Single(_fixture.Store.Saved);
        Assert.Equal(1, _fixture.Downloader.Count);
    }

    [Fact]
    public async Task OnlyOneInstallationRunsAtATimeAndTheOtherIsToldToTryAgain()
    {
        var gate = new TaskCompletionSource();
        _fixture.Downloader.Before = _ => gate.Task;
        var first = Install(_fixture.Serve());
        await Task.Delay(150);

        var second = await Install(_fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0", id: "other"));
        gate.SetResult();
        var firstOutcome = await first;

        Assert.Equal(InstallFailure.Busy, second.Failure);
        Assert.True(firstOutcome.IsInstalled);
        Assert.Single(_fixture.Store.Saved);
    }

    // ---- the web locks ----

    [Fact]
    public async Task WhileLocalOnlyIsOnNothingFromTheWebIsDownloaded()
    {
        using var locked = new InstallFixture(localOnly: true);
        var candidate = locked.Serve();

        var outcome = await locked.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.WebLocked, outcome.Failure);
        Assert.Contains("Local Only", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, locked.Downloader.Count);
        Assert.Empty(locked.Leftovers());
    }

    [Fact]
    public async Task WithTheWebPermissionOffNothingIsDownloaded()
    {
        using var locked = new InstallFixture(webPermission: false);

        var outcome = await locked.Installer.InstallAsync(locked.Serve());

        Assert.Equal(InstallFailure.WebLocked, outcome.Failure);
        Assert.Equal(0, locked.Downloader.Count);
    }

    [Fact]
    public async Task APackageServedFromThisPcNeedsNoWebSoItInstallsWhileLocalOnlyIsOn()
    {
        using var locked = new InstallFixture(localOnly: true, webPermission: false);
        var bundle = SampleBundle.Bytes();
        var url = "http://127.0.0.1:5003/notes.mcpb";
        locked.Downloader.Files[url] = bundle;
        var candidate = SampleBundle.Candidate(bundle, url: url);

        var outcome = await locked.Installer.InstallAsync(candidate);

        Assert.True(outcome.IsInstalled);
    }

    [Fact]
    public async Task AHostedServerNeedsNoWebToBeRecordedSoItIsRecordedWhileLocalOnlyIsOn()
    {
        using var locked = new InstallFixture(localOnly: true);

        var outcome = await locked.Installer.InstallAsync(Remote());

        Assert.True(outcome.IsInstalled);
        Assert.Equal(0, locked.Downloader.Count);
        Assert.Equal(0, locked.Clients.CreateCalls);
        Assert.Empty(locked.Runner.Runs);
    }

    // ---- keys ----

    [Fact]
    public async Task AnIntegrationThatAsksForAKeyIsInstalledButNotStartedAndSaysItNeedsSigningIn()
    {
        var candidate = _fixture.Serve(secrets: ["NOTES_API_KEY"]);

        var outcome = await Install(candidate);

        Assert.True(outcome.IsInstalled);
        Assert.Contains("asks for a key", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Clients.CreateCalls);
        var integration = Assert.Single(_fixture.Store.Saved);
        Assert.Equal(IntegrationAuthKind.EnvironmentSecret, integration.Authentication.Kind);
        Assert.Equal(IntegrationAuthState.NeedsSignIn, integration.Authentication.State);
        var binding = Assert.Single(integration.Authentication.Secrets);
        Assert.Equal("NOTES_API_KEY", binding.Target);
        Assert.Equal("notes.notes-api-key", binding.SecretName);
        Assert.Empty(_fixture.Secrets.Secrets);
        Assert.Empty(integration.Capabilities.ToolNames);
    }

    [Fact]
    public void SecretNamesAreAcceptedByTheSecretStore()
    {
        Assert.True(Assistant.Core.Contracts.SecretNames.IsValid(IntegrationInstaller.SecretName("notes", "NOTES_API_KEY")));
        Assert.True(Assistant.Core.Contracts.SecretNames.IsValid(IntegrationInstaller.SecretName("notes", new string('X', 200))));
    }

    // ---- npm ----

    private static InstallCandidate Npm(byte[] tarball, string id = "todoist", string version = "13.4.0", IReadOnlyList<string>? secrets = null)
    {
        var source = new InstallSource
        {
            Kind = InstallSourceKind.Npm,
            Identifier = "@doist/todoist-mcp",
            Version = version,
            DownloadUrl = $"https://registry.npmjs.org/@doist/todoist-mcp/-/todoist-mcp-{version}.tgz",
            Hash = TestArchives.Sha512(tarball),
            EntryName = "todoist-mcp",
            DependencyCount = 3,
        };
        return new InstallCandidate
        {
            Id = id,
            AppName = "Todoist",
            Name = "io.github.Doist/todoist-mcp",
            Source = source,
            Trust = CandidateTrust.VerifiedVendor,
            Publisher = "Doist",
            Runtime = CandidateRuntime.NodeJs,
            RequiredSecrets = secrets ?? [],
            Authentication = secrets is { Count: > 0 } ? IntegrationAuthKind.EnvironmentSecret : IntegrationAuthKind.None,
            Capability = new IntegrationCapability(CapabilityAction.Create, "task"),
            Evidence = CapabilityEvidence.ToolListed,
            FoundIn = ["mcp-registry"],
            ReviewedAt = Reviewable.Now,
            Fingerprint = InstallCandidate.FingerprintOf(id, "Todoist", source),
        };
    }

    // What `npm install` would leave: the package, its package.json and the program its bin names.
    private static void PretendNpmInstalled(ProcessSpec spec, string binJson = "{\"todoist-mcp\":\"dist/index.js\"}", string program = "dist/index.js")
    {
        var prefix = spec.Arguments[spec.Arguments.ToList().IndexOf("--prefix") + 1];
        var package = Path.Combine(prefix, "node_modules", "@doist", "todoist-mcp");
        Directory.CreateDirectory(Path.Combine(package, Path.GetDirectoryName(program.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty));
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@doist/todoist-mcp\",\"version\":\"13.4.0\",\"bin\":" + binJson + "}");
        File.WriteAllText(Path.Combine(package, program.Replace('/', Path.DirectorySeparatorChar)), "// server");
    }

    private InstallCandidate ServeNpm(string version = "13.4.0", IReadOnlyList<string>? secrets = null)
    {
        var tarball = Encoding.UTF8.GetBytes("pretend tarball " + version);
        var candidate = Npm(tarball, version: version, secrets: secrets);
        _fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;
        return candidate;
    }

    private static StubMcpClient TodoistServer(InstalledIntegration integration) => InstallFixture.Server("add-tasks", "find-tasks");

    [Fact]
    public async Task AnNpmPackageIsInstalledWithScriptsTurnedOffFromTheRegistryAloneAndStartedByTheManagedNode()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = spec =>
        {
            PretendNpmInstalled(spec);
            return new ProcessResult(0, false, false);
        };
        var tarball = Encoding.UTF8.GetBytes("pretend tarball");
        var candidate = Npm(tarball);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.True(outcome.IsInstalled, outcome.Message);
        var run = Assert.Single(fixture.Runner.Runs);
        var node = fixture.Runtimes.Find(RuntimeKind.NodeJs)!;
        Assert.Equal(node.ExecutablePath, run.FileName);
        var arguments = run.Arguments.ToList();
        Assert.EndsWith(Path.Combine("node_modules", "npm", "bin", "npm-cli.js"), arguments[0], StringComparison.Ordinal);
        Assert.Equal("install", arguments[1]);
        Assert.EndsWith("package.tgz", arguments[2], StringComparison.Ordinal);
        Assert.Contains("--ignore-scripts", arguments);
        Assert.Contains("--no-audit", arguments);
        Assert.Equal("https://registry.npmjs.org/", arguments[arguments.IndexOf("--registry") + 1]);
        Assert.Contains("--userconfig", arguments);
        Assert.Contains("--globalconfig", arguments);

        // npm refuses to load one file as both the user's and the global configuration (found live): each is a file of its own, and both are empty.
        var userConfig = arguments[arguments.IndexOf("--userconfig") + 1];
        var globalConfig = arguments[arguments.IndexOf("--globalconfig") + 1];
        Assert.NotEqual(userConfig, globalConfig);
        Assert.Equal(userConfig.StartsWith(fixture.Paths.IntegrationsDirectory, StringComparison.OrdinalIgnoreCase), globalConfig.StartsWith(fixture.Paths.IntegrationsDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(run.Environment);
        Assert.True(run.Timeout > TimeSpan.Zero && run.Timeout <= TimeSpan.FromMinutes(10));

        var integration = Assert.Single(fixture.Store.Saved);
        Assert.Equal(node.ExecutablePath, integration.Transport.Command);
        var entry = Assert.Single(integration.Transport.Arguments);
        Assert.EndsWith(Path.Combine("@doist", "todoist-mcp", "dist", "index.js"), entry, StringComparison.Ordinal);
        Assert.StartsWith(Path.Combine(fixture.Paths.IntegrationsDirectory, "todoist", "13.4.0"), integration.Transport.WorkingDirectory!, StringComparison.Ordinal);
        Assert.Equal(RuntimeKind.NodeJs, integration.Managed!.Runtime);
        Assert.Equal(node.Version, integration.Managed.RuntimeVersion);
        Assert.Equal(["add-tasks", "find-tasks"], integration.Capabilities.ToolNames);
        Assert.Equal([RuntimeKind.NodeJs], fixture.Runtimes.Ensured);
    }

    [Fact]
    public async Task ARuntimeThatIsThereIsReusedByTheNextIntegration()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = spec =>
        {
            PretendNpmInstalled(spec);
            return new ProcessResult(0, false, false);
        };
        fixture.Runtimes.MakeReady(RuntimeKind.NodeJs);
        var tarball = Encoding.UTF8.GetBytes("pretend tarball");
        var candidate = Npm(tarball);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;

        var plan = await fixture.Installer.PlanAsync(candidate);

        Assert.DoesNotContain(plan.Downloads, download => download.IsRuntime);
        Assert.Contains(plan.ReusedRuntimes, name => name.Contains("Node.js", StringComparison.Ordinal));
        Assert.Contains(plan.Downloads, download => !download.IsRuntime);
    }

    [Fact]
    public async Task APlanSaysWhichRuntimeWouldBeDownloadedAndHowBig()
    {
        var tarball = Encoding.UTF8.GetBytes("t");
        var candidate = Npm(tarball);

        var plan = await _fixture.Installer.PlanAsync(candidate);

        var runtime = Assert.Single(plan.Downloads, download => download.IsRuntime);
        Assert.Equal("Node.js 24", runtime.Name);
        Assert.Equal(36, runtime.Megabytes);
        Assert.False(plan.NeedsAdministrator);
        Assert.Null(plan.Blocker);
    }

    [Fact]
    public async Task APlanForACandidateThatBreaksARuleSaysWhy()
    {
        var plan = await _fixture.Installer.PlanAsync(Npm([1]) with { Id = "Bad Id" });

        Assert.NotNull(plan.Blocker);
        Assert.Empty(plan.Downloads);
    }

    [Fact]
    public async Task AFailedDependencyInstallLeavesNothingAndSaysSo()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = spec =>
        {
            Directory.CreateDirectory(Path.Combine(spec.WorkingDirectory, "partial"));
            return new ProcessResult(1, false, false);
        };
        var tarball = Encoding.UTF8.GetBytes("pretend tarball");
        var candidate = Npm(tarball);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.SetupFailed, outcome.Failure);
        Assert.Empty(fixture.Leftovers());
        Assert.Empty(fixture.Store.Saved);
    }

    [Fact]
    public async Task ADependencyInstallThatTakesTooLongIsEndedAndSaysSo()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = _ => new ProcessResult(-1, true, false);
        var tarball = Encoding.UTF8.GetBytes("pretend tarball");
        var candidate = Npm(tarball);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.SetupFailed, outcome.Failure);
        Assert.Contains("too long", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Leftovers());
    }

    [Theory]
    [InlineData("{\"x\":\"../../../evil.js\"}", "dist/index.js")]
    [InlineData("{\"x\":\"C:\\\\evil.js\"}", "dist/index.js")]
    [InlineData("{\"x\":\"dist/tool.exe\"}", "dist/tool.exe")]
    [InlineData("{\"x\":\"dist/tool.cmd\"}", "dist/tool.cmd")]
    [InlineData("{\"x\":\"dist/missing.js\"}", "dist/index.js")]
    [InlineData("{}", "dist/index.js")]
    public async Task AnNpmPackageWhoseProgramIsOutsideItselfOrNotAScriptIsNotInstalled(string bin, string program)
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = spec =>
        {
            PretendNpmInstalled(spec, bin, program);
            return new ProcessResult(0, false, false);
        };
        var tarball = Encoding.UTF8.GetBytes("pretend tarball");
        var candidate = Npm(tarball);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.EntryPointMissing, outcome.Failure);
        Assert.Empty(fixture.Leftovers());
    }

    [Fact]
    public async Task ARuntimeThatCannotBeSetUpFailsTheInstallationBeforeAnythingIsUnpacked()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runtimes.EnsureFails = new InstallException(InstallFailure.RuntimeUnavailable, "I could not set up what it needs.");
        var tarball = Encoding.UTF8.GetBytes("pretend tarball");
        var candidate = Npm(tarball);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = tarball;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.RuntimeUnavailable, outcome.Failure);
        Assert.Equal(0, fixture.Downloader.Count);
        Assert.Empty(fixture.Leftovers());
    }

    // ---- PyPI ----

    private static InstallCandidate PyPi(byte[] wheel)
    {
        var source = new InstallSource
        {
            Kind = InstallSourceKind.PyPi,
            Identifier = "todoist-mcp",
            Version = "0.4.2",
            DownloadUrl = "https://files.pythonhosted.org/packages/aa/bb/todoist_mcp-0.4.2-py3-none-any.whl",
            Hash = TestArchives.Sha256(wheel),
        };
        return new InstallCandidate
        {
            Id = "todoist",
            AppName = "Todoist",
            Name = "todoist-mcp",
            Source = source,
            Runtime = CandidateRuntime.Python,
            Capability = new IntegrationCapability(CapabilityAction.Create, "task"),
            Evidence = CapabilityEvidence.Described,
            FoundIn = ["pypi"],
            ReviewedAt = Reviewable.Now,
            Fingerprint = InstallCandidate.FingerprintOf("todoist", "Todoist", source),
        };
    }

    private static ProcessResult PretendPython(ProcessSpec spec)
    {
        var arguments = spec.Arguments.ToList();
        if (arguments.Contains("venv"))
        {
            var venv = arguments[^1];
            Directory.CreateDirectory(Path.Combine(venv, "Scripts"));
            File.WriteAllText(Path.Combine(venv, "Scripts", "python.exe"), "python");
        }
        else if (arguments.Contains("pip"))
        {
            var venv = Path.GetDirectoryName(Path.GetDirectoryName(spec.FileName))!;
            var info = Path.Combine(venv, "Lib", "site-packages", "todoist_mcp-0.4.2.dist-info");
            Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, "entry_points.txt"), "[console_scripts]\ntodoist-mcp = todoist_mcp:main\nother-tool = todoist_mcp:other\n\n[other]\nx = y:z\n");
            File.WriteAllText(Path.Combine(venv, "Scripts", "todoist-mcp.exe"), "MZ");
        }

        return new ProcessResult(0, false, false);
    }

    [Fact]
    public async Task APyPiPackageIsInstalledIntoAnEnvironmentOfItsOwnFromWheelsOnlyAndStartedByItsConsoleScript()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = PretendPython;
        var wheel = Encoding.UTF8.GetBytes("pretend wheel");
        var candidate = PyPi(wheel);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = wheel;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.True(outcome.IsInstalled, outcome.Message);
        Assert.Equal(2, fixture.Runner.Runs.Count);
        var python = fixture.Runtimes.Find(RuntimeKind.Python)!;
        Assert.Equal(python.ExecutablePath, fixture.Runner.Runs[0].FileName);
        Assert.Equal(["-m", "venv"], fixture.Runner.Runs[0].Arguments.Take(2));
        var pip = fixture.Runner.Runs[1];
        Assert.EndsWith(Path.Combine("venv", "Scripts", "python.exe"), pip.FileName, StringComparison.Ordinal);
        var arguments = pip.Arguments.ToList();
        Assert.Equal(["-m", "pip", "--isolated", "install"], arguments.Take(4));
        Assert.Contains("--only-binary=:all:", arguments);
        Assert.Contains("--no-input", arguments);
        Assert.Equal("https://pypi.org/simple", arguments[arguments.IndexOf("--index-url") + 1]);
        Assert.EndsWith(".whl", arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(arguments, argument => argument.Contains("--no-binary", StringComparison.Ordinal));

        var integration = Assert.Single(fixture.Store.Saved);
        Assert.EndsWith(Path.Combine("venv", "Scripts", "todoist-mcp.exe"), integration.Transport.Command, StringComparison.Ordinal);
        Assert.Empty(integration.Transport.Arguments);
        Assert.Equal(RuntimeKind.Python, integration.Managed!.Runtime);
        Assert.Equal(IntegrationSourceKind.CommunityRegistry, integration.Source.Kind);
    }

    [Fact]
    public async Task APyPiPackageWithNoConsoleScriptIsNotInstalled()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        fixture.Runner.Behaviour = spec =>
        {
            var result = PretendPython(spec);
            if (spec.Arguments.Contains("pip"))
            {
                var venv = Path.GetDirectoryName(Path.GetDirectoryName(spec.FileName))!;
                File.Delete(Path.Combine(venv, "Scripts", "todoist-mcp.exe"));
            }

            return result;
        };
        var wheel = Encoding.UTF8.GetBytes("pretend wheel");
        var candidate = PyPi(wheel);
        fixture.Downloader.Files[candidate.Source.DownloadUrl!] = wheel;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.EntryPointMissing, outcome.Failure);
        Assert.Empty(fixture.Leftovers());
    }

    [Fact]
    public async Task APyPiFileThatIsNotAWheelByNameIsNotInstalled()
    {
        using var fixture = new InstallFixture(client: TodoistServer);
        var wheel = Encoding.UTF8.GetBytes("pretend");
        var candidate = PyPi(wheel);
        var source = candidate.Source with { DownloadUrl = "https://files.pythonhosted.org/packages/aa/bb/todoist-mcp-0.4.2.tar.gz" };
        candidate = candidate with { Source = source, Fingerprint = InstallCandidate.FingerprintOf(candidate.Id, candidate.AppName, source) };
        fixture.Downloader.Files[source.DownloadUrl!] = wheel;

        var outcome = await fixture.Installer.InstallAsync(candidate);

        Assert.Equal(InstallFailure.NotAllowed, outcome.Failure);
        Assert.Empty(fixture.Runner.Runs);
    }

    // ---- a hosted server ----

    private static InstallCandidate Remote(IReadOnlyList<string>? secrets = null, IntegrationAuthKind kind = IntegrationAuthKind.None)
    {
        var source = new InstallSource { Kind = InstallSourceKind.Remote, Identifier = "https://ai.todoist.net/mcp" };
        return new InstallCandidate
        {
            Id = "todoist",
            AppName = "Todoist",
            Name = "com.todoist/mcp",
            Source = source,
            Trust = CandidateTrust.VerifiedVendor,
            RequiredSecrets = secrets ?? [],
            Authentication = kind,
            Capability = new IntegrationCapability(CapabilityAction.Create, "task"),
            ReviewedAt = Reviewable.Now,
            FoundIn = ["mcp-registry"],
            Fingerprint = InstallCandidate.FingerprintOf("todoist", "Todoist", source),
        };
    }

    [Fact]
    public async Task AHostedServerIsRecordedByItsAddressAndNothingIsDownloadedOrStarted()
    {
        var outcome = await Install(Remote());

        Assert.True(outcome.IsInstalled);
        var integration = Assert.Single(_fixture.Store.Saved);
        Assert.Equal(McpTransportKind.StreamableHttp, integration.Transport.Kind);
        Assert.Equal("https://ai.todoist.net/mcp", integration.Transport.Endpoint);
        Assert.Null(integration.Transport.Command);
        Assert.Null(integration.InstalledVersion);
        Assert.True(integration.Permissions.LeavesThisPc);
        Assert.Equal(0, _fixture.Downloader.Count);
        Assert.Equal(0, _fixture.Clients.CreateCalls);
        Assert.Empty(_fixture.Runner.Runs);
        Assert.Empty(_fixture.Leftovers());
    }

    [Fact]
    public async Task AHostedServerThatWantsABearerTokenNamesTheAuthorizationHeaderAndAsksForSigningIn()
    {
        await Install(Remote(["Authorization"], IntegrationAuthKind.BearerToken));

        var integration = Assert.Single(_fixture.Store.Saved);
        Assert.Equal(IntegrationAuthKind.BearerToken, integration.Authentication.Kind);
        Assert.Equal(IntegrationAuthState.NeedsSignIn, integration.Authentication.State);
        Assert.Equal("Authorization", Assert.Single(integration.Authentication.Secrets).Target);
    }

    // ---- an update ----

    [Fact]
    public async Task AnUpdateIsInstalledBesideTheOldVersionAndOnlyThenReplacesItAndTheOldFilesGo()
    {
        await Install(_fixture.Serve());
        await _fixture.Registry.UpdateAsync("notes", integration => integration with
        {
            Enabled = false,
            Permissions = integration.Permissions with { ReadOnlyTools = ["list_notes"], TrustToolAnnotations = true },
        });
        var oldDirectory = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0");
        var updated = _fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0");

        var outcome = await _fixture.Installer.UpdateAsync(updated, "notes");

        Assert.True(outcome.IsInstalled, outcome.Message);
        Assert.Contains("Updated Notes to version 1.1.0", outcome.Message, StringComparison.Ordinal);
        var integration = Assert.Single(_fixture.Store.Saved);
        Assert.Equal("1.1.0", integration.InstalledVersion);
        Assert.Contains(Path.Combine("notes", "1.1.0"), integration.Transport.Command, StringComparison.Ordinal);
        Assert.Equal("1.1.0", integration.Managed!.Package is null ? null : integration.InstalledVersion);
        Assert.False(integration.Enabled);
        Assert.Equal(["list_notes"], integration.Permissions.ReadOnlyTools);
        Assert.True(integration.Permissions.TrustToolAnnotations);
        Assert.False(Directory.Exists(oldDirectory));
        Assert.True(Directory.Exists(Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.1.0")));
        Assert.Equal(["notes"], _fixture.Connections.Forgotten);
    }

    [Fact]
    public async Task AnUpdateLetsGoOfTheConnectionBeforeTheOldFilesAreDeleted()
    {
        await Install(_fixture.Serve());
        var oldDirectory = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0");
        var existedWhenForgotten = false;
        _fixture.Connections.OnForget = _ =>
        {
            existedWhenForgotten = Directory.Exists(oldDirectory);
            return Task.CompletedTask;
        };

        await _fixture.Installer.UpdateAsync(_fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0"), "notes");

        Assert.True(existedWhenForgotten);
        Assert.False(Directory.Exists(oldDirectory));
    }

    [Fact]
    public async Task AFailedUpdateLeavesTheInstalledVersionExactlyAsItWas()
    {
        await Install(_fixture.Serve());
        var before = Assert.Single(_fixture.Store.Saved);
        var updated = _fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0");
        _fixture.Downloader.Files[updated.Source.DownloadUrl!] = SampleBundle.Bytes("1.1.0", type: "binary", args: "[\"--tampered\"]");

        var outcome = await _fixture.Installer.UpdateAsync(updated, "notes");

        Assert.Equal(InstallFailure.HashMismatch, outcome.Failure);
        Assert.Equal(before, Assert.Single(_fixture.Store.Saved));
        Assert.True(Directory.Exists(Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.0.0")));
        Assert.False(Directory.Exists(Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes", "1.1.0")));
        Assert.Empty(_fixture.Connections.Forgotten);
    }

    [Fact]
    public async Task AnUpdateThatDoesNotStartLeavesTheInstalledVersionAlone()
    {
        var starts = 0;
        using var fixture = new InstallFixture(client: _ => ++starts == 1 ? InstallFixture.Server() : new StubMcpClient { ConnectFailure = new McpException(McpFailure.LaunchFailed), TransportKind = McpTransportKind.Stdio });
        await fixture.Installer.InstallAsync(fixture.Serve());
        var before = Assert.Single(fixture.Store.Saved);

        var outcome = await fixture.Installer.UpdateAsync(fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0"), "notes");

        Assert.Equal(InstallFailure.DidNotStart, outcome.Failure);
        Assert.Equal(before, Assert.Single(fixture.Store.Saved));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.IntegrationsDirectory, "notes", "1.0.0")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.IntegrationsDirectory, "notes", "1.1.0")));
    }

    [Fact]
    public async Task AnUpdateToTheVersionThatIsInstalledIsNotAnUpdate()
    {
        var candidate = _fixture.Serve();
        await Install(candidate);

        var outcome = await _fixture.Installer.UpdateAsync(candidate, "notes");

        Assert.Equal(InstallFailure.NotAllowed, outcome.Failure);
    }

    [Fact]
    public async Task AnIntegrationTheUserAddedThemselvesCannotBeUpdatedByTheInstaller()
    {
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes"));

        var outcome = await _fixture.Installer.UpdateAsync(_fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0"), "notes");

        Assert.Equal(InstallFailure.NotAllowed, outcome.Failure);
        Assert.Equal(0, _fixture.Downloader.Count);
    }

    [Fact]
    public async Task AnUpdateSaysWhichToolsTheNewVersionNoLongerOffers()
    {
        var starts = 0;
        using var fixture = new InstallFixture(client: _ => ++starts == 1 ? InstallFixture.Server("create_note", "list_notes", "delete_note") : InstallFixture.Server("create_note", "list_notes"));
        await fixture.Installer.InstallAsync(fixture.Serve());

        var outcome = await fixture.Installer.UpdateAsync(fixture.Serve(SampleBundle.Bytes("1.1.0"), version: "1.1.0"), "notes");

        Assert.True(outcome.IsInstalled);
        Assert.Contains("no longer offers `delete_note`", outcome.Message, StringComparison.Ordinal);
    }

    // ---- clean-up of what was left ----

    [Fact]
    public async Task CleaningUpDeletesWhatAnInterruptedInstallationLeft()
    {
        await Install(_fixture.Serve());
        var integrations = _fixture.Paths.IntegrationsDirectory;
        Directory.CreateDirectory(Path.Combine(integrations, ".staging", "abc"));
        File.WriteAllText(Path.Combine(integrations, ".staging", "abc", "x.bin"), "x");
        Directory.CreateDirectory(Path.Combine(integrations, "ghost", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(integrations, "notes", "0.9.0"));

        var removed = await _fixture.Installer.CleanUpAsync();

        Assert.Equal(3, removed);
        Assert.False(Directory.Exists(Path.Combine(integrations, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(integrations, "ghost")));
        Assert.False(Directory.Exists(Path.Combine(integrations, "notes", "0.9.0")));
        Assert.True(Directory.Exists(Path.Combine(integrations, "notes", "1.0.0")));
        Assert.Single(_fixture.Store.Saved);
    }

    [Fact]
    public async Task CleaningUpDoesNothingWhileAnInstallationRuns()
    {
        var gate = new TaskCompletionSource();
        _fixture.Downloader.Before = _ => gate.Task;
        var running = Install(_fixture.Serve());
        await Task.Delay(150);
        Directory.CreateDirectory(Path.Combine(_fixture.Paths.IntegrationsDirectory, "ghost", "1.0.0"));

        var removed = await _fixture.Installer.CleanUpAsync();
        gate.SetResult();
        await running;

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(Path.Combine(_fixture.Paths.IntegrationsDirectory, "ghost")));
    }

    // ---- only ever inside the assistant's own folders ----

    [Fact]
    public async Task EverythingAnInstalledIntegrationStartsLivesInsideTheAssistantsFolders()
    {
        await Install(_fixture.Serve());

        var integration = Assert.Single(_fixture.Store.Saved);
        Assert.True(_fixture.Layout.IsInsideIntegrations(integration.Transport.Command!));
        Assert.True(_fixture.Layout.IsInsideIntegrations(integration.Transport.WorkingDirectory!));
        Assert.Empty(IntegrationRules.Problems(integration));
    }

    [Fact]
    public void TheLayoutRefusesAnIdOrAVersionThatCouldReachOutsideItsFolder()
    {
        Assert.Throws<ArgumentException>(() => _fixture.Layout.IntegrationDirectory("..\\other"));
        Assert.Throws<ArgumentException>(() => _fixture.Layout.IntegrationDirectory("a/b"));
        Assert.Throws<ArgumentException>(() => _fixture.Layout.VersionDirectory("notes", "..\\..\\x"));
        Assert.Throws<ArgumentException>(() => _fixture.Layout.VersionDirectory("notes", "1.0.0/../.."));
        Assert.Throws<ArgumentException>(() => _fixture.Layout.RuntimeDirectory(RuntimeKind.NodeJs, "..\\x"));
    }

    [Fact]
    public void AnInstalledIntegrationNeverNeedsAdministratorRights()
    {
        Assert.False(new InstallPlan().NeedsAdministrator);
    }
}
