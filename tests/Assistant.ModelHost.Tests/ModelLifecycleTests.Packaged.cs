using System.Security.Cryptography;
using Assistant.Core.Assets;
using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.ModelHost.FakeEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// A model that came packaged with the Assistant (step 123): loaded from the packaged folder only when its files are the ones the manifest lists,
/// through the real resolver, service, lifecycle and host. A model that does not match never reaches the host.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    [Fact]
    public async Task APackagedModelThatMatchesItsManifest_IsLoadedFromThePackagedFolder()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var (service, packaged, _) = PackagedService(setup, out var lifecycleHost);
        await using var host = lifecycleHost;
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = service(lifecycle);

        var chunks = await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        Assert.Equal("Hello there!", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Equal("chat-4b", lifecycle.Model?.Id);
        Assert.Equal(Path.Combine(packaged, "chat-4b", "model.gguf"), ModelArgument(Path.Combine(packaged, "chat-4b", "model.gguf")));
    }

    [Fact]
    public async Task APackagedModelWhoseFileChangedWithoutChangingSize_IsNeverSentToTheHost()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var (service, packaged, _) = PackagedService(setup, out var lifecycleHost);
        await using var host = lifecycleHost;
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = service(lifecycle);
        var modelFile = Path.Combine(packaged, "chat-4b", "model.gguf");
        var bytes = File.ReadAllBytes(modelFile);
        bytes[^2] ^= 0x01;
        File.WriteAllBytes(modelFile, bytes);

        var failure = await Assert.ThrowsAsync<ModelHostException>(() => ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout())));

        Assert.Equal(ModelHostErrorCode.FilesFailedCheck, failure.Code);
        Assert.Equal(AssetStatusText.ModelFilesFailedText, ModelErrorText.Describe(failure));
        Assert.Equal(0, host.Launcher.Started);
        Assert.Null(lifecycle.Model);
        Assert.DoesNotContain(packaged, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APackagedModelMissingAFileItsManifestListsWithTheProjectorGone_IsNotLoadedAsATextModel()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var (service, packaged, _) = PackagedService(setup, out var lifecycleHost, projector: true);
        await using var host = lifecycleHost;
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = service(lifecycle);
        File.Delete(Path.Combine(packaged, "chat-4b", "mmproj.gguf"));

        var failure = await Assert.ThrowsAsync<ModelHostException>(() => ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout())));

        Assert.Equal(ModelHostErrorCode.FilesFailedCheck, failure.Code);
        Assert.Equal(0, host.Launcher.Started);
    }

    [Fact]
    public async Task AModelInTheUsersOwnFolder_NeedsNoManifest_AndIsUsedInsteadOfThePackagedOne()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var (service, packaged, paths) = PackagedService(setup, out var lifecycleHost);
        await using var host = lifecycleHost;
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = service(lifecycle);

        // The packaged model is damaged, but the user's own model of the same profile wins and is not listed anywhere.
        File.WriteAllText(Path.Combine(packaged, "chat-4b", "model.gguf"), "damaged");
        var own = Path.Combine(paths.ModelsDirectory, "chat-4b");
        Directory.CreateDirectory(own);
        File.Copy(setup.ScenarioPath, Path.Combine(own, "model.gguf"));

        var chunks = await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        Assert.Equal("Hello there!", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Equal(Path.Combine(own, "model.gguf"), ModelArgument(Path.Combine(own, "model.gguf")));
    }

    [Fact]
    public async Task AFilePickedByHand_IsNotCheckedAgainstAnyManifest()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var (_, packaged, paths) = PackagedService(setup, out var lifecycleHost);
        await using var host = lifecycleHost;
        await using var lifecycle = host.CreateLifecycle(out _);
        File.WriteAllText(Path.Combine(packaged, "chat-4b", "model.gguf"), "damaged");
        var assets = new PackagedAssets(new PackagedAssetPaths(Path.Combine(setup.Directory, "install")));
        var models = new LocalModelService(
            lifecycle,
            new FixedSettings(new AppSettings { Model = new ModelSettings { ModelFilePath = setup.ScenarioPath } }),
            new ModelProfileResolver(new ModelProfileCatalog(), new EnvironmentHardwareInfoProvider(), paths),
            NullLogger<LocalModelService>.Instance,
            assets);

        var chunks = await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        Assert.Equal("Hello there!", string.Concat(chunks.Select(chunk => chunk.Text)));
    }

    // The default profile packaged under <scenario folder>\install\assets\models, with a manifest of what was put there. Returns the service
    // (once given its lifecycle), the packaged models folder and the user's own paths.
    private static (Func<IModelLifecycle, LocalModelService> Service, string Packaged, AppPaths Paths) PackagedService(
        FakeEngineSetup setup, out TestHost host, bool projector = false)
    {
        var install = new PackagedAssetPaths(Path.Combine(setup.Directory, "install"));
        var folder = Path.Combine(install.ModelsDirectory, "chat-4b");
        Directory.CreateDirectory(folder);
        var files = new List<AssetFile>();
        File.Copy(setup.ScenarioPath, Path.Combine(folder, "model.gguf"));
        files.Add(Describe(folder, "model.gguf"));
        if (projector)
        {
            File.WriteAllText(Path.Combine(folder, "mmproj.gguf"), FakeEngineScenario.Magic + "-projector");
            files.Add(Describe(folder, "mmproj.gguf"));
        }

        File.WriteAllText(install.ManifestOf(AssetKind.Model), new AssetManifest([new AssetGroup("chat-4b", files)]).ToJson());
        var paths = new AppPaths(Path.Combine(setup.Directory, "data"));
        host = new TestHost(setup);
        var assets = new PackagedAssets(install, new JsonAssetCheckCache(Path.Combine(setup.Directory, "asset-checks.json")));
        return (
            lifecycle => new LocalModelService(
                lifecycle,
                new FixedSettings(new AppSettings()),
                new ModelProfileResolver(new ModelProfileCatalog(), new EnvironmentHardwareInfoProvider(), paths, null, null, install.ModelsDirectory),
                NullLogger<LocalModelService>.Instance,
                assets),
            install.ModelsDirectory,
            paths);
    }

    private static AssetFile Describe(string folder, string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder, name));
        return new AssetFile(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    // Where the fake engine says it was told to load the model from; it writes its report beside the model it loaded.
    private static string ModelArgument(string loadedModel)
    {
        var report = FakeEngineReport.Load(loadedModel, 1);
        return report.Arguments[report.Arguments.ToList().IndexOf("--model") + 1];
    }
}
