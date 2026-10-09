using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.Core.Models;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// A downloaded model and its vision projector (PROJECT_SPEC §5.4): the projector that lies beside the model is the model's, whether the settings
/// name it or not, and a user who would rather not keep it in memory has it loaded only for a question that carries a picture.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    private static readonly ModelRequest PictureQuestion = Question with { Images = [SampleMessages.PrivateImage] };

    // The 4B models were downloaded without a projector, and set up in the settings without one. Once the projector is downloaded beside the model
    // it must read pictures without the model being set up again.
    [Fact]
    public async Task AProjectorBesideADownloadedModel_IsLoadedWithIt_ThoughTheSettingsDoNotNameIt()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var model = setup.CreateScenario(DownloadCatalog.ModelFileName, new FakeEngineScenario());
        var projector = setup.CreateGguf(DownloadCatalog.ProjectorFileName);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = VisionService(lifecycle, new ModelSettings { ModelFilePath = model });

        Assert.True((await models.GetActiveModelAsync(TestPipes.Timeout()))!.SupportsVision);
        await ReadAllAsync(models.GenerateAsync(PictureQuestion, TestPipes.Timeout()));

        Assert.True(lifecycle.Model!.SupportsVision);
        Assert.Equal(projector, ValueAfter(FakeEngineReport.Load(model, 1).Arguments, "--mmproj"));
    }

    [Fact]
    public async Task AModelFileOfTheUsersOwn_IsNotGivenAProjectorItWasNotSetUpWith()
    {
        // Only a model under the name the Assistant's own downloads have is taken to be one of them: any other file is the user's, set up as they set it up.
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var model = setup.CreateScenario("my-own-model.gguf", new FakeEngineScenario());
        setup.CreateGguf(DownloadCatalog.ProjectorFileName);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = VisionService(lifecycle, new ModelSettings { ModelFilePath = model });

        Assert.False((await models.GetActiveModelAsync(TestPipes.Timeout()))!.SupportsVision);
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        Assert.DoesNotContain("--mmproj", FakeEngineReport.Load(model, 1).Arguments);
    }

    [Fact]
    public async Task WithPicturesReadOnlyWhenNeeded_TheProjectorIsLoadedForAQuestionWithAPicture_AndPutAwayAgainAfterAWhile()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var model = setup.CreateScenario(DownloadCatalog.ModelFileName, new FakeEngineScenario());
        var projector = setup.CreateGguf(DownloadCatalog.ProjectorFileName);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = VisionService(lifecycle, new ModelSettings { ModelFilePath = model, VisionOnDemand = true }, rest: TimeSpan.FromMilliseconds(150));

        // An ordinary question loads the model without the projector; it reads pictures all the same, so that a picture is sent to it.
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.DoesNotContain("--mmproj", FakeEngineReport.Load(model, 1).Arguments);
        Assert.False(lifecycle.Model!.SupportsVision);
        Assert.True((await models.GetActiveModelAsync(TestPipes.Timeout()))!.SupportsVision);

        // A question with a picture loads it again, with the projector.
        var answer = await ReadAllAsync(models.GenerateAsync(PictureQuestion, TestPipes.Timeout()));
        Assert.Equal("Hello there!", string.Concat(answer.Select(chunk => chunk.Text)));
        Assert.Equal(projector, ValueAfter(FakeEngineReport.Load(model, 2).Arguments, "--mmproj"));
        Assert.True(lifecycle.Model!.SupportsVision);

        // And with no picture asked about for a while, it is loaded once more without it, ahead of the next question.
        await models.VisionResting.WaitAsync(TestPipes.Timeout());
        Assert.DoesNotContain("--mmproj", FakeEngineReport.Load(model, 3).Arguments);
        Assert.False(lifecycle.Model!.SupportsVision);
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.False(File.Exists(FakeEngineReport.PathOf(model, 4)));
    }

    [Fact]
    public async Task WithPicturesReadAtOnce_TheProjectorStaysLoaded()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var model = setup.CreateScenario(DownloadCatalog.ModelFileName, new FakeEngineScenario());
        setup.CreateGguf(DownloadCatalog.ProjectorFileName);
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = VisionService(lifecycle, new ModelSettings { ModelFilePath = model }, rest: TimeSpan.FromMilliseconds(50));

        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        await ReadAllAsync(models.GenerateAsync(PictureQuestion, TestPipes.Timeout()));
        await models.VisionResting.WaitAsync(TestPipes.Timeout());
        await Task.Delay(200);

        Assert.True(lifecycle.Model!.SupportsVision);
        Assert.False(File.Exists(FakeEngineReport.PathOf(model, 2)));
    }

    private static LocalModelService VisionService(IModelLifecycle lifecycle, ModelSettings model, TimeSpan? rest = null) =>
        new(lifecycle, new FixedSettings(new AppSettings { Model = model }), new NoModelProfiles(), NullLogger<LocalModelService>.Instance)
        {
            VisionRest = rest ?? TimeSpan.FromMinutes(3),
        };
}
