using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Assistant.Core.Assets;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The packaging scripts (step 122) are Windows PowerShell 5.1 and batch files that a person runs without a build, so what breaks them quietly is
/// guarded here: a character PowerShell 5.1 would read in the wrong code page, a script that does not parse, and the numbers and rules they share with
/// the app's own asset reader. What they do when run (a package built, installed, upgraded, damaged, uninstalled) was run for real, and is described in
/// packaging/README.md.
/// </summary>
public sealed class PackagingScriptsTests
{
    private static readonly string[] Scripts =
    [
        "PackageCommon.ps1", "Add-PackagedAssets.ps1", "Build-Package.ps1", "Install-Assistant.ps1", "Uninstall-Assistant.ps1",
    ];

    private static string Packaging([CallerFilePath] string testFile = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(testFile)))!, "packaging");

    private static string Read(string name) => File.ReadAllText(Path.Combine(Packaging(), name));

    [Fact]
    public void EveryScriptAndNoteOfThePackagingFolder_IsPlainAsciiWithoutAByteOrderMark()
    {
        // The README for developers is markdown and may hold other characters; what a person runs or opens in Notepad may not.
        foreach (var file in Directory.GetFiles(Packaging()).Where(file => !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = File.ReadAllBytes(file);

            // Windows PowerShell 5.1 reads a script without a byte order mark in the ANSI code page, which would corrupt any other character.
            Assert.DoesNotContain(bytes, value => value > 126);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, Path.GetFileName(file));
        }
    }

    [Fact]
    public void TheBatchFilesHaveWindowsLineEndings()
    {
        foreach (var name in new[] { "Install-Assistant.cmd", "Uninstall-Assistant.cmd" })
        {
            var text = File.ReadAllText(Path.Combine(Packaging(), name));

            Assert.DoesNotMatch(new Regex("(?<!\r)\n"), text);
            Assert.Contains("-ExecutionPolicy Bypass", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryScriptParsesInWindowsPowerShell()
    {
        var parse = string.Concat(Scripts.Select(script =>
            $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{Path.Combine(Packaging(), script)}', [ref]$null, [ref]$e); " +
            $"if ($e.Count) {{ '{script}: ' + $e[0].Message; $failed = 1 }}; ")) + "exit [int]($failed -eq 1)";
        using var process = Process.Start(new ProcessStartInfo(
            "powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(parse)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));

        Assert.True(process.ExitCode == 0, output);
    }

    [Fact]
    public void TheScriptsAndTheAppAgreeOnTheManifestFormatAndItsLimits()
    {
        var common = Read("PackageCommon.ps1");

        Assert.Contains($"$script:AssetManifestSchemaVersion = {AssetManifest.SchemaVersion}", common, StringComparison.Ordinal);
        Assert.Contains($"$Path.Length -gt {AssetManifestReader.MaxPathLength}", common, StringComparison.Ordinal);
        Assert.Contains($"$files.Count -gt {AssetManifestReader.MaxFilesPerGroup}", common, StringComparison.Ordinal);
        Assert.Contains($"$Id.Length -le 64", common, StringComparison.Ordinal);

        // The device names the app refuses in a path are the ones the script refuses.
        var listed = Regex.Match(common, @"\$devices = @\((?<names>[^)]*)\)", RegexOptions.Singleline).Groups["names"].Value;
        var names = Regex.Matches(listed, @"'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        foreach (var device in new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT9" })
        {
            Assert.Contains(device, names);
            Assert.Null(AssetManifestReader.NormalizePath(device + ".bin"));
        }

        Assert.Equal(23, names.Length);
    }

    [Fact]
    public void ThePackageHoldsTheAssetsInTheFolderTheAppLooksIn_AndTheModelAndVoiceNamesAreTheApps()
    {
        var install = Read("Install-Assistant.ps1");
        var add = Read("Add-PackagedAssets.ps1");
        var paths = new PackagedAssetPaths(Path.Combine(Path.GetTempPath(), "install"));

        // <install>\assets\models and \voices, each with manifest.json.
        Assert.Equal("assets", PackagedAssetPaths.DirectoryName);
        Assert.Contains("(Join-Path $install 'assets')", install, StringComparison.Ordinal);
        Assert.Contains("@('models', 'voices')", install, StringComparison.Ordinal);
        Assert.Equal("models", Path.GetFileName(paths.ModelsDirectory));
        Assert.Equal("voices", Path.GetFileName(paths.VoicesDirectory));
        Assert.Contains($"'{PackagedAssetPaths.ManifestFileName}'", add, StringComparison.Ordinal);

        // In the package they are in the program's own folder, beside Assistant.UI.exe (app\assets), which is where PackagedAssetPaths looks, so a
        // package folder run as it is finds them; not in a folder of their own beside app\, which only an installer would have carried over.
        var build = Read("Build-Package.ps1");
        Assert.Contains("$assetsFolder = Join-Path $app 'assets'", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $package 'assets'", build, StringComparison.Ordinal);
        Assert.Contains("Join-Path $package 'app\\assets'", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $package 'assets'", install, StringComparison.Ordinal);

        // A model is stored as model.gguf and mmproj.gguf, which is what the built-in profiles name.
        Assert.Contains("'model.gguf'", add, StringComparison.Ordinal);
        Assert.Contains("'mmproj.gguf'", add, StringComparison.Ordinal);
        Assert.EndsWith("model.gguf", Assistant.Core.ModelProfiles.ModelProfileCatalog.Standard.ModelPath, StringComparison.Ordinal);
        Assert.EndsWith("mmproj.gguf", Assistant.Core.ModelProfiles.ModelProfileCatalog.Standard.ProjectorPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerNeverTouchesTheUsersDataByDefault()
    {
        var install = Read("Install-Assistant.ps1");
        var uninstall = Read("Uninstall-Assistant.ps1");

        // The data folder is only read when installing, and only deleted when asked, after checking its name.
        Assert.DoesNotMatch(new Regex(@"Remove-Item[^\r\n]*\$data\b"), install);
        Assert.Contains("[switch]$RemoveUserData", uninstall, StringComparison.Ordinal);
        Assert.Contains("Test-SafeDirectoryName -Directory $data -LeafName 'Assistant'", uninstall, StringComparison.Ordinal);

        // It closes a copy that runs from the install folder, and only one: a copy run from elsewhere is never ended.
        Assert.Contains("Get-AssistantProcesses", Read("PackageCommon.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUninstallRemovesTheDataFromInsideARealInstallFolder_WithTheUninstallerItselfStillRunningInIt()
    {
        // What the native uninstaller's run looks like, which a made-up install folder that only holds a manifest does not show: the script is started
        // from <install>\tools; the folder holds the app's own .NET assemblies, named as the .NET Framework's are; unins000.exe waits in it for the
        // copy of itself that does the work; and a helper of the Assistant may still be on its way out. Twice the uninstall stopped here without
        // removing anything: unins000.exe was taken for the Assistant still running, and the helper type that closes windows did not compile beside
        // the app's System.Core.dll. Everything here is a made-up installation in a temporary folder; the stored secrets are left alone (-KeepSecrets).
        var root = Path.Combine(Path.GetTempPath(), "assistant-uninstall-test-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Programs", "Assistant");
        var data = Path.Combine(root, "Assistant");
        var menu = Path.Combine(root, "Start Menu", "Programs");
        Directory.CreateDirectory(Path.Combine(install, "tools"));
        Directory.CreateDirectory(Path.Combine(data, "models", "downloads"));
        Directory.CreateDirectory(Path.Combine(data, "sockets"));
        Directory.CreateDirectory(menu);
        var stray = new List<Process>();
        try
        {
            File.WriteAllText(Path.Combine(install, "package-manifest.json"), """{"product":"Assistant"}""");
            foreach (var script in new[] { "Uninstall-Assistant.ps1", "PackageCommon.ps1" })
            {
                File.Copy(Path.Combine(Packaging(), script), Path.Combine(install, "tools", script));
            }

            // The app is self-contained: its folder has .NET's own System.dll and System.Core.dll, which only pass types on to other assemblies.
            var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            foreach (var name in new[] { "System.dll", "System.Core.dll", "mscorlib.dll", "netstandard.dll", "System.Runtime.dll" })
            {
                File.Copy(Path.Combine(runtime, name), Path.Combine(install, name));
            }

            File.WriteAllText(Path.Combine(menu, "Assistant.lnk"), "shortcut");
            File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(data, "models", "downloads", "model.gguf"), "model");
            var readOnly = Path.Combine(data, "models", "downloads", "kept.bin");
            File.WriteAllText(readOnly, "read-only");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            // Something of another program's, reached from inside the data folder by a link (a runtime's shared cache is linked this way): removing the
            // data removes the link and never what it leads to. A model of another app (Handy's) once seemed to have gone with an uninstall.
            var elsewhere = Path.Combine(root, "Elsewhere", "hub");
            Directory.CreateDirectory(elsewhere);
            var theirs = Path.Combine(elsewhere, "their-model.gguf");
            File.WriteAllText(theirs, "another program's model");
            using (var link = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(data, "linked")}\" \"{elsewhere}\"")
                   {
                       UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                   })!)
            {
                link.WaitForExit(10_000);
                Assert.True(Directory.Exists(Path.Combine(data, "linked")), "The link could not be made.");
            }

            // Two programs that run from the install folder: the uninstaller's waiting process, and a helper the Assistant left behind.
            var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            foreach (var name in new[] { "unins000.exe", "Assistant.Stray.exe" })
            {
                var program = Path.Combine(install, name);
                File.Copy(ping, program);
                stray.Add(Process.Start(new ProcessStartInfo(program, "-n 300 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!);
            }

            var start = new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(install, "tools", "Uninstall-Assistant.ps1") + "\"" +
                    $" -InstallDirectory \"{install}\" -DataDirectory \"{data}\" -StartMenuDirectory \"{menu}\" -PreserveInstallDirectory -Quiet -Force -RemoveUserData -KeepSecrets",

                // As the uninstaller of 0.1.137 and earlier started it: in the install folder itself.
                WorkingDirectory = install,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Its temporary folder is the test's own, so that the log it writes, and the copy of itself it carries on from, are not left in the user's.
            var temporary = Path.Combine(root, "temp");
            Directory.CreateDirectory(temporary);
            start.Environment["TEMP"] = temporary;
            start.Environment["TMP"] = temporary;
            using var uninstall = Process.Start(start)!;
            var error = uninstall.StandardError.ReadToEndAsync();
            var output = await uninstall.StandardOutput.ReadToEndAsync() + await error;
            Assert.True(uninstall.WaitForExit(120_000), "The uninstall did not finish.");

            Assert.True(uninstall.ExitCode == 0, output);
            Assert.False(Directory.Exists(data), "The data folder was left. " + output);
            Assert.True(File.Exists(theirs), "What a link in the data folder led to was deleted with it.");
            Assert.Equal("another program's model", File.ReadAllText(theirs));
            Assert.Empty(Directory.GetDirectories(root, "Assistant.removing-*"));
            Assert.False(File.Exists(Path.Combine(menu, "Assistant.lnk")), "The shortcut was left. " + output);

            // What it did is written where it can be read afterwards: the uninstaller runs it with no window.
            var log = File.ReadAllText(Path.Combine(temporary, "Assistant-uninstall.log"));
            Assert.Contains("The data folder was removed.", log, StringComparison.Ordinal);
            Assert.Contains("The Assistant was removed.", log, StringComparison.Ordinal);
            Assert.DoesNotContain("failed", log, StringComparison.OrdinalIgnoreCase);

            // The uninstaller was not ended, nor waited for; the Assistant's own helper was.
            Assert.False(stray[0].HasExited, "The uninstaller's own process was ended.");
            Assert.True(stray[1].WaitForExit(5_000), "The helper the Assistant left behind was not closed.");
        }
        finally
        {
            foreach (var process in stray)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5_000);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A program that was still closing held a file: the folder is in the temporary folder, and is not this test's to wait for.
            }
        }
    }

    [Fact]
    public void TheInstallerStartsTheAssistantOnlyAfterItsLastPageIsClosed()
    {
        var setup = Read("Assistant.iss");

        // The install script is never asked to start the Assistant: it runs before the last page, and setup would open over the installer.
        Assert.DoesNotContain("-Launch", setup, StringComparison.Ordinal);

        // It is started when setup is done, which is after Finish, and only when the user ticked the box.
        var done = setup.IndexOf("if CurStep = ssDone then", StringComparison.Ordinal);
        var installing = setup.IndexOf("if CurStep <> ssPostInstall then", StringComparison.Ordinal);
        Assert.True(done > 0 && installing > done);
        var afterFinish = setup[done..installing];
        Assert.Contains("WizardIsTaskSelected('launch')", afterFinish, StringComparison.Ordinal);
        Assert.Contains(@"Exec(ExpandConstant('{app}\Assistant.UI.exe')", afterFinish, StringComparison.Ordinal);
        Assert.Contains("ewNoWait", afterFinish, StringComparison.Ordinal);

        // The script still can, for an install from the package folder without the installer.
        Assert.Contains("if ($Launch) { Start-Process", Read("Install-Assistant.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUninstallerStartsItsScriptOutsideTheInstallFolder_AndSaysWhereWhatWentWrongIsWritten()
    {
        var setup = Read("Assistant.iss");
        var common = Read("PackageCommon.ps1");
        var uninstall = Read("Uninstall-Assistant.ps1");

        // The script is not started with the install folder as its working folder, and moves out of it by itself when an older uninstaller starts it there.
        Assert.Contains("GetUninstallCleanupParameters(), ExpandConstant('{sys}'), SW_HIDE", setup, StringComparison.Ordinal);
        Assert.Contains("[Environment]::CurrentDirectory = [Environment]::SystemDirectory", uninstall, StringComparison.Ordinal);

        // No helper is compiled against an assembly named without its path, which the compiler would look for beside the script first.
        Assert.DoesNotContain("-ReferencedAssemblies", common, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"^\s*Add-Type -Language", RegexOptions.Multiline), common.Replace("        Add-Type -Language CSharp -TypeDefinition $Source -ErrorAction Stop", string.Empty, StringComparison.Ordinal));

        // The uninstaller is never one of the Assistant's processes, and a failure leaves something to read.
        Assert.Contains(@"'^unins\d+$'", common, StringComparison.Ordinal);
        Assert.Contains("Assistant-uninstall.log", common, StringComparison.Ordinal);
        Assert.Contains(@"{%TEMP}\Assistant-uninstall.log", setup, StringComparison.Ordinal);
        Assert.Contains("Write-UninstallLog ('Uninstall failed: '", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommunityIntegrationIsBundled_AndTheMadeUpSampleIsLeftOutUnlessAsked()
    {
        var build = Read("Build-Package.ps1");

        Assert.Contains("-not $IncludeSamples", build, StringComparison.Ordinal);
        Assert.Contains("Assistant.SampleMcpServer.*", build, StringComparison.Ordinal);
        Assert.DoesNotContain("npm", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pip install", build, StringComparison.OrdinalIgnoreCase);
    }
}
