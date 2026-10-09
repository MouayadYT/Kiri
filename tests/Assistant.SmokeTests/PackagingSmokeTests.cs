using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Assistant.Core.Assets;
using Assistant.SmokeTests.Support;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklists 14 and 15: the package, and a clean install and uninstall. The package is the real one (built by <c>packaging\Build-Package.ps1</c>, or the one
/// named in <c>ASSISTANT_SMOKE_PACKAGE_DIR</c>); it is installed into a folder of the check's own with its data folder and Start menu in the same place, and
/// removed again with the real scripts. The user's own data and registry are protected: File Explorer's entry and the browsers' hosts are never registered
/// (<c>-SkipIntegrations</c>) and are compared before and after, the start-with-Windows entry the installer repoints is put back, and <c>-RemoveUserData</c>,
/// which also deletes the user's stored secrets, is never used. The installed app is not started: it would open the user's own data folder, so that is
/// the person's check (RELEASE_CHECKLIST.md).
/// </summary>
public sealed class PackagingSmokeTests(PackageFixture package) : IClassFixture<PackageFixture>
{
    private static readonly string[] Programs =
    [
        "Assistant.UI.exe", "Assistant.ModelHost.exe", "Assistant.ExplorerExtension.exe", "Assistant.BrowserBridge.exe", "Assistant.ico",
        @"llama.cpp\llama-server.exe", @"browser-extension\manifest.json", @"tools\Uninstall-Assistant.ps1", @"tools\Add-PackagedAssets.ps1", "hostfxr.dll",
    ];

    private static readonly string[] PackageRootFiles =
        ["Install-Assistant.cmd", "Install-Assistant.ps1", "Uninstall-Assistant.cmd", "Uninstall-Assistant.ps1", "PackageCommon.ps1", "README.txt", "package-manifest.json"];

    [Fact]
    public async Task ThePackageIsSelfContainedAndWhole_WithAManifestThatMatchesEveryFileAndTheSpecsVersion()
    {
        foreach (var name in PackageRootFiles)
        {
            Assert.True(File.Exists(Path.Combine(package.Folder, name)), name + " is not in the package.");
        }

        var app = Path.Combine(package.Folder, "app");
        foreach (var name in Programs)
        {
            Assert.True(File.Exists(Path.Combine(app, name)), name + " is not in the package's program files.");
        }

        // Self-contained (its own .NET runtime, so the PC needs none), with no debug files and without the demos' made-up connected app.
        Assert.True(File.Exists(Path.Combine(app, "coreclr.dll")), "The .NET runtime is not in the package.");
        Assert.Empty(Directory.GetFiles(app, "*.pdb", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(app, "Assistant.SampleMcpServer.*"));

        // The manifest names the version in version.txt, and the size and SHA-256 of every program file, and no file is missing from it.
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package.Folder, "package-manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("Assistant", root.GetProperty("product").GetString());
        Assert.Equal("win-x64", root.GetProperty("runtime").GetString());
        var specVersion = (await File.ReadAllTextAsync(Repo.Combine("version.txt"))).Trim();
        Assert.Matches(@"^\d+\.\d+\.\d+$", specVersion);
        Assert.Equal(specVersion, root.GetProperty("version").GetString());
        Assert.StartsWith(specVersion, FileVersionInfo.GetVersionInfo(Path.Combine(app, "Assistant.UI.exe")).ProductVersion, StringComparison.Ordinal);

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!.Replace('/', '\\');
            var path = Path.Combine(package.Folder, relative);
            listed.Add(relative);
            Assert.True(File.Exists(path), relative + " is listed in the manifest but is not in the package.");
            Assert.Equal(file.GetProperty("size").GetInt64(), new FileInfo(path).Length);
            await using var stream = File.OpenRead(path);
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant());
        }

        // Every program file is listed. The files of the models and voices (app\assets\models\<id>\..., app\assets\voices\<id>\...) are not: each group is
        // checked against the manifest of its kind, which is listed, and that is what the Assistant itself checks them with.
        var unlisted = Directory.GetFiles(app, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(package.Folder, file)).Where(file => !listed.Contains(file) && !IsAssetGroupFile(file)).ToArray();
        Assert.Empty(unlisted);
    }

    private static bool IsAssetGroupFile(string relativeToPackage) =>
        new[] { @"app\assets\models\", @"app\assets\voices\" }.Any(prefix =>
            relativeToPackage.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(relativeToPackage, prefix + "manifest.json", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task ThePackagesProgramFolderHoldsItsModelsAndVoicesBesideTheProgram_SoItFindsThemWithoutInstalling()
    {
        // The complaint this guards: the models and voices were in a folder of their own beside app\, so a copy run from the package folder saw none.
        var app = Path.Combine(package.Folder, "app");
        Assert.False(Directory.Exists(Path.Combine(package.Folder, "assets")), "The package has an assets folder beside the app folder; it belongs inside it, beside Assistant.UI.exe.");

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package.Folder, "package-manifest.json")));
        var hasModels = manifest.RootElement.GetProperty("hasModels").GetBoolean();
        var hasVoices = manifest.RootElement.GetProperty("hasVoices").GetBoolean();
        if (package.BuiltHere)
        {
            Assert.True(hasModels && hasVoices, "The package built for this check should hold a model and a voice.");
        }

        // The Assistant's own lookup, pointed at the folder Assistant.UI.exe is in: what the program does when it is started from there.
        using var assets = new PackagedAssets(new PackagedAssetPaths(app));
        Assert.Equal(hasModels, assets.GroupIds(AssetKind.Model).Count > 0);
        Assert.Equal(hasVoices, assets.GroupIds(AssetKind.Voice).Count > 0);
        foreach (var kind in new[] { AssetKind.Model, AssetKind.Voice })
        {
            foreach (var id in assets.GroupIds(kind))
            {
                var state = await assets.VerifyAsync(kind, id);
                Assert.True(state.IsVerified, $"{kind} {id} was {state.Status} where the program is.");
            }
        }
    }

    [Fact]
    public async Task ThePackagedHelperProgramsRunFromThePackage_WithoutAnInstalledDotNet()
    {
        // Started with nothing to do, each ends at once with its usage code; that they start at all is the check, with the PC's own .NET hidden from them.
        foreach (var (name, usage) in new[] { ("Assistant.ExplorerExtension.exe", 2), ("Assistant.ModelHost.exe", 2) })
        {
            var info = new ProcessStartInfo(Path.Combine(package.Folder, "app", name)) { UseShellExecute = false, CreateNoWindow = true };
            info.Environment["DOTNET_ROOT"] = Path.Combine(Path.GetTempPath(), "no-dotnet-here");
            info.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            info.Environment["PATH"] = Environment.SystemDirectory;
            using var process = Process.Start(info)!;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(limit.Token);
            Assert.True(usage == process.ExitCode, $"{name} ended with {process.ExitCode}, not its usage code {usage}.");
        }
    }

    [Fact]
    public async Task AnInstallIsWholeAndSeparateFromTheUsersData_AndTheUninstallRemovesItAllAndLeavesTheUsersRegistryAndDataAlone()
    {
        using var scratch = new ScratchFolder();
        Directory.CreateDirectory(scratch.Path);
        using var registry = new UserRegistry(scratch.Path);
        var install = Path.Combine(scratch.Path, "Assistant");
        var data = Path.Combine(scratch.Path, "data");
        var menu = Path.Combine(scratch.Path, "menu");
        Directory.CreateDirectory(data);
        var settingsFile = Path.Combine(data, "settings.json");
        await File.WriteAllTextAsync(settingsFile, "{\"marker\":\"the user's own settings\"}");

        // The Settings > Apps entry has one fixed name, so it is made only where there is none to overwrite; the start-with-Windows entry the
        // installer repoints (when the user has it on) is put back by the registry guard.
        var addsAppsEntry = !UserRegistry.HasAppsEntry();
        var installing = new List<string> { "-InstallDirectory", install, "-DataDirectory", data, "-StartMenuDirectory", menu, "-SkipIntegrations" };
        if (!addsAppsEntry)
        {
            installing.Add("-SkipUninstallEntry");
        }

        var installed = await Scripts.RunAsync(Path.Combine(package.Folder, "Install-Assistant.ps1"), TimeSpan.FromMinutes(5), [.. installing]);
        Assert.True(installed.ExitCode == 0, "The install failed:\n" + installed.Output);

        // What was installed is the package: every program file, the manifest, the shortcut, the uninstaller; and the data folder was not touched.
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package.Folder, "package-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!.Replace('/', '\\');
            if (relative.StartsWith(@"app\", StringComparison.OrdinalIgnoreCase))
            {
                var installedFile = Path.Combine(install, relative[4..]);
                Assert.True(File.Exists(installedFile), relative + " was not installed.");
                Assert.Equal(file.GetProperty("size").GetInt64(), new FileInfo(installedFile).Length);
            }
        }

        Assert.True(File.Exists(Path.Combine(install, "package-manifest.json")));
        Assert.True(File.Exists(Path.Combine(install, "tools", "Uninstall-Assistant.ps1")));

        // The models and voices of the package's app\assets arrive in the install folder's own, beside the installed program.
        foreach (var kind in new[] { "models", "voices" })
        {
            var packaged = Path.Combine(package.Folder, "app", "assets", kind, "manifest.json");
            if (File.Exists(packaged))
            {
                Assert.True(File.Exists(Path.Combine(install, "assets", kind, "manifest.json")), $"The {kind} were not installed beside the program.");
            }
        }

        Assert.True(File.Exists(Path.Combine(menu, "Assistant.lnk")), "The Start menu shortcut was not made.");
        Assert.Equal("{\"marker\":\"the user's own settings\"}", await File.ReadAllTextAsync(settingsFile));
        if (addsAppsEntry)
        {
            Assert.Equal(install, UserRegistry.AppsEntryLocation(), ignoreCase: true);
        }

        // The uninstaller of the installed copy, as Settings > Apps runs it: nothing asked, nothing of the user's removed.
        var removed = await Scripts.RunAsync(
            Path.Combine(install, "tools", "Uninstall-Assistant.ps1"), TimeSpan.FromMinutes(5),
            "-InstallDirectory", install, "-DataDirectory", data, "-StartMenuDirectory", menu, "-Quiet");
        Assert.True(removed.ExitCode == 0, "The uninstall failed:\n" + removed.Output);

        Assert.False(Directory.Exists(install), "The install folder was left behind.");
        Assert.False(File.Exists(Path.Combine(menu, "Assistant.lnk")), "The Start menu shortcut was left behind.");
        if (addsAppsEntry)
        {
            Assert.Null(UserRegistry.AppsEntryLocation());
        }

        // What the app registers in the user's registry is not the installer's to add, and the uninstaller removes only what points into its own folder.
        Assert.Equal(registry.Integrations, UserRegistry.DescribeIntegrations());
        Assert.False(
            UserRegistry.RunEntry()?.Contains(install, StringComparison.OrdinalIgnoreCase) == true,
            "A start-with-Windows entry that points into the removed install folder was left behind.");

        // The user's data is theirs: the default is to keep it.
        Assert.Equal("{\"marker\":\"the user's own settings\"}", await File.ReadAllTextAsync(settingsFile));
    }

    [Fact]
    public async Task AnIncompleteDamagedOrUnsafeInstallIsRefused_AndNothingIsChanged()
    {
        using var scratch = new ScratchFolder();
        using var registry = new UserRegistry(scratch.Path);
        Directory.CreateDirectory(scratch.Path);

        // A package that is not whole (its program files are not there to check): nothing is installed, and the installer says why.
        var incomplete = Path.Combine(scratch.Path, "incomplete");
        Directory.CreateDirectory(incomplete);
        foreach (var name in new[] { "Install-Assistant.ps1", "PackageCommon.ps1", "package-manifest.json" })
        {
            File.Copy(Path.Combine(package.Folder, name), Path.Combine(incomplete, name));
        }

        var target = Path.Combine(scratch.Path, "target");
        var data = Path.Combine(scratch.Path, "data");
        var damaged = await Scripts.RunAsync(
            Path.Combine(incomplete, "Install-Assistant.ps1"), TimeSpan.FromMinutes(2),
            "-InstallDirectory", target, "-DataDirectory", data, "-StartMenuDirectory", Path.Combine(scratch.Path, "menu"), "-SkipIntegrations", "-SkipUninstallEntry");
        Assert.Equal(1, damaged.ExitCode);
        Assert.Contains("damaged or incomplete", damaged.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target), "A package that was not whole was partly installed.");

        // A folder that is not an Assistant install and not empty is never mirrored over, and the user's data folder is never an install folder.
        var other = Path.Combine(scratch.Path, "other");
        Directory.CreateDirectory(other);
        var keep = Path.Combine(other, "keep.txt");
        await File.WriteAllTextAsync(keep, "not the Assistant's");
        var notEmpty = await Scripts.RunAsync(
            Path.Combine(incomplete, "Install-Assistant.ps1"), TimeSpan.FromMinutes(2),
            "-InstallDirectory", other, "-DataDirectory", data, "-StartMenuDirectory", Path.Combine(scratch.Path, "menu"), "-SkipIntegrations", "-SkipUninstallEntry");
        Assert.Equal(1, notEmpty.ExitCode);
        Assert.Equal("not the Assistant's", await File.ReadAllTextAsync(keep));

        var ontoData = await Scripts.RunAsync(
            Path.Combine(incomplete, "Install-Assistant.ps1"), TimeSpan.FromMinutes(2),
            "-InstallDirectory", data, "-DataDirectory", data, "-StartMenuDirectory", Path.Combine(scratch.Path, "menu"), "-SkipIntegrations", "-SkipUninstallEntry");
        Assert.Equal(1, ontoData.ExitCode);
        Assert.Contains("not a place to install into", ontoData.Output, StringComparison.Ordinal);
        Assert.Equal(registry.Integrations, UserRegistry.DescribeIntegrations());
    }
}
