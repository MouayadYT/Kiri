using System.Reflection.PortableExecutable;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>Finding the bundled llama.cpp runtime and checking that Windows can load it.</summary>
public sealed class BundledRuntimeLocatorTests
{
    // Of the fourteen CPU backends, the copies keep the baseline one; every other runtime file is needed.
    private const string KeptCpuBackend = "ggml-cpu-x64.dll";

    // The Windows DLLs the bundled build imports, other than the Visual C++ runtime's (third_party/llama.cpp/README.md).
    private static readonly string[] WindowsDlls =
        ["KERNEL32.dll", "WS2_32.dll", "CRYPT32.dll", "SHELL32.dll", "ADVAPI32.dll", "PSAPI.DLL"];

    [Fact]
    public void BundledRuntime_IsReady()
    {
        var status = TestRuntimes.Bundled.Locate();

        Assert.Equal(ModelRuntimeState.Ready, status.State);
        Assert.True(status.IsReady);
        Assert.Empty(status.MissingFiles);
        Assert.Equal(TestRuntimes.BundledDirectory, status.Runtime.Directory);
        Assert.Equal(Path.Combine(TestRuntimes.BundledDirectory, "llama-server.exe"), status.Runtime.ServerPath);
        Assert.True(File.Exists(status.Runtime.ServerPath));
        Assert.Equal("The local model runtime is ready.", status.Message);
    }

    [Fact]
    public void ServerExecutable_IsAnX64ImageThatImportsTheServerLibrary()
    {
        var image = NativeImage.Read(Path.Combine(TestRuntimes.BundledDirectory, "llama-server.exe"));

        Assert.Equal(Machine.Amd64, image.Machine);
        Assert.Contains("llama-server-impl.dll", image.Imports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("KERNEL32.dll", image.Imports, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingFolder_IsNotInstalled()
    {
        var status = Locate(Path.Combine(Path.GetTempPath(), "no-such-runtime-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(ModelRuntimeState.NotInstalled, status.State);
        Assert.Null(status.Runtime);
    }

    [Fact]
    public void MissingServer_IsNotInstalled()
    {
        using var copy = RuntimeCopy.Create();
        File.Delete(copy.PathOf("llama-server.exe"));

        Assert.Equal(ModelRuntimeState.NotInstalled, Locate(copy.Directory).State);
    }

    [Fact]
    public void TrimmedCopy_IsStillReady()
    {
        using var copy = RuntimeCopy.Create();

        var status = Locate(copy.Directory);

        Assert.Equal(ModelRuntimeState.Ready, status.State);
        Assert.Equal(copy.PathOf("llama-server.exe"), status.Runtime!.ServerPath);
    }

    [Theory]
    [InlineData("llama-server-impl.dll")]
    [InlineData("llama.dll")]
    [InlineData("ggml-base.dll")]
    [InlineData("libomp.dll")]
    public void MissingNativeFile_IsIncomplete_AndNamed(string fileName)
    {
        using var copy = RuntimeCopy.Create();
        File.Delete(copy.PathOf(fileName));

        var status = Locate(copy.Directory);

        Assert.Equal(ModelRuntimeState.Incomplete, status.State);
        Assert.Equal([fileName], status.MissingFiles);
        Assert.Null(status.Runtime);
    }

    [Fact]
    public void NoCpuBackend_IsIncomplete()
    {
        using var copy = RuntimeCopy.Create();
        File.Delete(copy.PathOf(KeptCpuBackend));

        var status = Locate(copy.Directory);

        Assert.Equal(ModelRuntimeState.Incomplete, status.State);
        Assert.Equal([ModelRuntimeLayout.CpuBackendPattern], status.MissingFiles);
    }

    [Fact]
    public void MissingVisualCppRuntime_IsAMissingSystemComponent()
    {
        using var copy = RuntimeCopy.Create();
        var system = Path.Combine(copy.Directory, "system");
        Directory.CreateDirectory(system);
        foreach (var dll in WindowsDlls)
        {
            File.WriteAllBytes(Path.Combine(system, dll), []);
        }

        var status = Locate(copy.Directory, system);

        Assert.Equal(ModelRuntimeState.MissingSystemComponent, status.State);
        Assert.Equal(["MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll"], status.MissingFiles);
        Assert.Contains("Visual C++", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerBuiltForAnotherMachine_IsIncompatible()
    {
        using var copy = RuntimeCopy.Create();
        var x86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "whoami.exe");
        File.Copy(x86, copy.PathOf("llama-server.exe"), overwrite: true);

        Assert.Equal(ModelRuntimeState.Incompatible, Locate(copy.Directory).State);
    }

    [Fact]
    public void DamagedLibrary_IsIncompatible()
    {
        using var copy = RuntimeCopy.Create();
        File.WriteAllText(copy.PathOf("llama.dll"), "not an image");

        Assert.Equal(ModelRuntimeState.Incompatible, Locate(copy.Directory).State);
    }

    [Fact]
    public void LibraryThatCannotBeOpened_IsInaccessible()
    {
        using var copy = RuntimeCopy.Create();
        using (new FileStream(copy.PathOf("llama-common.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(ModelRuntimeState.Inaccessible, Locate(copy.Directory).State);
        }

        Assert.Equal(ModelRuntimeState.Ready, Locate(copy.Directory).State);
    }

    [Fact]
    public void Status_IsLoggedWhenItChanges_WithoutPaths()
    {
        using var copy = RuntimeCopy.Create(folderName: "PRIVATE-PATH-0c5a");
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        var locator = new BundledRuntimeLocator(
            new ModelRuntimeOptions(copy.Directory), loggers.CreateLogger<BundledRuntimeLocator>());

        locator.Locate();
        locator.Locate();
        File.Delete(copy.PathOf("mtmd.dll"));
        var status = locator.Locate();

        Assert.Equal(2, capture.Entries.Count(entry => entry.Contains(" 2320 ", StringComparison.Ordinal) || entry.Contains(" 2321 ", StringComparison.Ordinal)));
        Assert.Contains("Model runtime is ready", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Model runtime is unavailable (Incomplete, 1 files missing)", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", status.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", locator.Locate().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-0c5a", new ModelRuntime(copy.Directory, copy.PathOf("x")).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryState_HasAPlainMessage_WithoutPaths()
    {
        foreach (var state in Enum.GetValues<ModelRuntimeState>())
        {
            var message = ModelRuntimeStateText.Describe(state);

            Assert.False(string.IsNullOrWhiteSpace(message), state.ToString());
            Assert.EndsWith(".", message, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", message, StringComparison.Ordinal);
        }
    }

    private static ModelRuntimeStatus Locate(string directory, string? systemDirectory = null)
    {
        var options = new ModelRuntimeOptions(directory);
        if (systemDirectory is not null)
        {
            options = options with { SystemDirectory = systemDirectory };
        }

        return new BundledRuntimeLocator(options, NullLogger<BundledRuntimeLocator>.Instance).Locate();
    }

    /// <summary>A copy of the bundled runtime with one CPU backend, which a test may damage.</summary>
    private sealed class RuntimeCopy : IDisposable
    {
        private readonly string _root;

        private RuntimeCopy(string root, string directory)
        {
            _root = root;
            Directory = directory;
        }

        public string Directory { get; }

        public static RuntimeCopy Create(string? folderName = null)
        {
            var root = TestRuntimes.NewTempDirectory();
            var directory = Path.Combine(root, folderName ?? ModelRuntimeLayout.DirectoryName);
            System.IO.Directory.CreateDirectory(directory);
            foreach (var file in System.IO.Directory.GetFiles(TestRuntimes.BundledDirectory, "*.*"))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith("ggml-cpu", StringComparison.OrdinalIgnoreCase) || name == KeptCpuBackend)
                {
                    File.Copy(file, Path.Combine(directory, name));
                }
            }

            return new RuntimeCopy(root, directory);
        }

        public string PathOf(string fileName) => Path.Combine(Directory, fileName);

        public void Dispose() => TestRuntimes.DeleteDirectory(_root);
    }
}
