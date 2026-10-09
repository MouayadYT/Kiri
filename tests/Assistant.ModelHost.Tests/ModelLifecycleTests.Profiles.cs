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
/// A model chosen by profile rather than by path: the models folder, the hardware preset and the profile's options
/// reaching the engine through the real lifecycle, client and session.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    [Fact]
    public async Task AnInstalledProfile_IsTheModelWhenNoFileWasPicked_LoadedWithItsPresetsOptions()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var paths = new AppPaths(setup.Directory);
        var installed = InstallDefaultProfile(setup, paths, projector: true);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = ProfileService(lifecycle, paths, new ModelSettings { HardwarePresetId = "compact" });

        var before = await models.GetActiveModelAsync(TestPipes.Timeout());
        var chunks = await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        // Before it loads the model is described by its profile; the profile's id is its identity throughout.
        Assert.Equal(new ModelInfo("chat-4b", 4096) { SupportsVision = true, SupportsConstrainedOutput = true, SupportsToolCalling = true }, before);
        Assert.Equal("chat-4b", lifecycle.Model?.Id);
        Assert.True(lifecycle.Model!.SupportsVision);
        Assert.Equal("Hello there!", string.Concat(chunks.Select(chunk => chunk.Text)));
        var arguments = FakeEngineReport.Load(installed, 1).Arguments;
        Assert.Equal("4096", ValueAfter(arguments, "--ctx-size"));
        Assert.Equal("512", ValueAfter(arguments, "--batch-size"));
        Assert.Equal("256", ValueAfter(arguments, "--ubatch-size"));
        Assert.Equal(Path.Combine(paths.ModelsDirectory, "chat-4b", "mmproj.gguf"), ValueAfter(arguments, "--mmproj"));
    }

    [Fact]
    public async Task TheGraphicsCardSetting_ReachesTheEngineAsTheProcessorAlone_AndOnIsTheEnginesOwnChoice()
    {
        foreach (var useGpu in new[] { false, true })
        {
            using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
            var paths = new AppPaths(setup.Directory);
            var installed = InstallDefaultProfile(setup, paths, projector: false);
            await using var host = new TestHost(setup);
            await using var lifecycle = host.CreateLifecycle(out _);
            var models = ProfileService(lifecycle, paths, new ModelSettings { UseGpuAcceleration = useGpu });

            await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

            var arguments = FakeEngineReport.Load(installed, 1).Arguments;
            if (useGpu)
            {
                Assert.DoesNotContain("--device", arguments);
            }
            else
            {
                Assert.Equal("none", ValueAfter(arguments, "--device"));
            }
        }
    }

    [Fact]
    public async Task AProfileWhoseProjectorIsMissing_LoadsAsATextModel()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var paths = new AppPaths(setup.Directory);
        var installed = InstallDefaultProfile(setup, paths, projector: false);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = ProfileService(lifecycle, paths, new ModelSettings());

        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        var arguments = FakeEngineReport.Load(installed, 1).Arguments;
        Assert.False(lifecycle.Model!.SupportsVision);
        Assert.DoesNotContain("--mmproj", arguments);
        Assert.Contains("--no-mmproj-auto", arguments);
    }

    [Fact]
    public async Task AProfileThatIsNotInstalled_IsNoModelAtAll_AndStartsNothing()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var paths = new AppPaths(setup.Directory);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = ProfileService(lifecycle, paths, new ModelSettings());

        Assert.Null(await models.GetActiveModelAsync(TestPipes.Timeout()));
        var failure = await Assert.ThrowsAsync<ModelHostException>(
            () => ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout())));

        Assert.Equal(ModelHostErrorCode.ModelNotFound, failure.Code);
        Assert.Equal(0, host.Launcher.Started);
    }

    [Fact]
    public async Task AFilePickedByHand_IsUsedAsItIs_WhateverTheProfileSays()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var paths = new AppPaths(setup.Directory);
        InstallDefaultProfile(setup, paths, projector: true);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = ProfileService(
            lifecycle, paths, new ModelSettings { ModelFilePath = setup.ScenarioPath, HardwarePresetId = "compact" });

        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        var arguments = setup.Report(1).Arguments;
        Assert.Equal("model", lifecycle.Model!.Id);
        Assert.Equal(ModelFiles.DefaultContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture), ValueAfter(arguments, "--ctx-size"));
        Assert.DoesNotContain("--batch-size", arguments);
        Assert.DoesNotContain("--mmproj", arguments);
    }

    [Fact]
    public async Task AConversationWithFiles_LoadsTheModelAgainWithTheLargerWindow_AndAnOrdinaryOneGoesBack()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var paths = new AppPaths(setup.Directory);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = ProfileService(lifecycle, paths, new ModelSettings { ModelFilePath = setup.ScenarioPath });

        // An ordinary conversation: the model is loaded with 8,000 tokens.
        var ordinary = await models.GetActiveModelAsync(TestPipes.Timeout());
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.Equal(8000, ordinary!.ContextLength);
        Assert.Equal("8000", ValueAfter(setup.Report(1).Arguments, "--ctx-size"));

        // The conversation being answered carries files: the model as it will be has the larger window before it is loaded with it, so that the
        // prompt is fitted to it, and the next generation loads it again with 32,000.
        Assert.True(models.Use(documents: true));
        var larger = await models.GetActiveModelAsync(TestPipes.Timeout());
        Assert.Equal(32000, larger!.ContextLength);
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.Equal("32000", ValueAfter(setup.Report(2).Arguments, "--ctx-size"));

        // While the conversation goes on, the model stays as it is.
        var loaded = lifecycle.Model;
        Assert.False(models.Use(documents: true));
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.Same(loaded, lifecycle.Model);

        // An ordinary conversation again: back to 8,000, and the memory the larger window took is given back.
        Assert.True(models.Use(documents: false));
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.Equal("8000", ValueAfter(setup.Report(3).Arguments, "--ctx-size"));
    }

    [Fact]
    public async Task AWindowTheUserSet_IsNotChangedByAConversationWithFiles()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var paths = new AppPaths(setup.Directory);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = ProfileService(lifecycle, paths, new ModelSettings { ModelFilePath = setup.ScenarioPath, ContextLength = 12288 });

        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        var loaded = lifecycle.Model;
        models.Use(documents: true);
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        Assert.Equal("12288", ValueAfter(setup.Report(1).Arguments, "--ctx-size"));
        Assert.Same(loaded, lifecycle.Model);
    }

    // The fake engine reads its scenario from the model file, so the profile's model file is a copy of it.
    // Returns the installed model file, which is also where the fake engine writes what it saw.
    private static string InstallDefaultProfile(FakeEngineSetup setup, AppPaths paths, bool projector)
    {
        var folder = Path.Combine(paths.ModelsDirectory, ModelProfileCatalog.DefaultProfileId);
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, "model.gguf");
        File.Copy(setup.ScenarioPath, model);
        if (projector)
        {
            File.WriteAllText(Path.Combine(folder, "mmproj.gguf"), FakeEngineScenario.Magic + "-projector");
        }

        return model;
    }

    private static LocalModelService ProfileService(IModelLifecycle lifecycle, AppPaths paths, ModelSettings model) =>
        new(
            lifecycle,
            new FixedSettings(new AppSettings { Model = model }),
            new ModelProfileResolver(new ModelProfileCatalog(), new EnvironmentHardwareInfoProvider(), paths),
            NullLogger<LocalModelService>.Instance);

    private static string ValueAfter(IReadOnlyList<string> arguments, string option) =>
        arguments[arguments.ToList().IndexOf(option) + 1];
}
