using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.ModelHosting;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The model window: the local model's status, load and unload ----------------------------------------------

    private static ModelStatusViewModel CreateModelViewModel(
        FakeModelLifecycle lifecycle, out AppEventBus bus, InMemorySettingsService? settings = null)
    {
        bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        return new ModelStatusViewModel(lifecycle, bus, settings ?? new InMemorySettingsService());
    }

    private static ModelPreviewWindow CreateModelWindow(ModelStatusViewModel viewModel)
    {
        var window = new ModelPreviewWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false, Topmost = false,
        };
        window.Show();
        return window;
    }

    // The lifecycle publishes from its own thread, and the view model hops to the UI thread to apply the change, so the
    // test publishes from a pool thread too and keeps the UI thread pumping until the event has been delivered.
    private static void Publish(AppEventBus bus, ModelStatusChanged status)
    {
        var publishing = Task.Run(() => bus.PublishAsync(status));
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!publishing.IsCompleted && DateTime.UtcNow < timeout)
        {
            Pump();
        }

        publishing.GetAwaiter().GetResult();
        Pump();
    }

    [Fact]
    public void AskingForTheDemoModelOpensTheModelWindowAndItIsListedWithTheDemos() => RunSta(() =>
    {
        var preview = new RecordingModelPreview();
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), modelPreview: preview);
        Assert.Contains("demo model", demo.Answer("demo")!.Text);

        var answer = demo.Answer("Demo model!");

        Assert.NotNull(answer);
        Assert.Contains("model window", answer.Text);
        Assert.Equal(1, preview.Shown);
        demo.Answer("demo text");
        Assert.Equal(1, preview.Shown);

        var alone = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now));
        Assert.Contains("not available", alone.Answer("demo model")!.Text);
    });

    [Fact]
    public void TheViewModelStartsFromTheLifecyclesStatusAndFollowsItsChanges() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var viewModel = CreateModelViewModel(lifecycle, out var bus);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal(ModelStatus.NotLoaded, viewModel.Status);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.CanUnload);
        Assert.False(viewModel.UnloadCommand.CanExecute(null));

        Publish(bus, new ModelStatusChanged(ModelStatus.Loading) { ModelId = "chat" });
        Assert.Equal(ModelStatus.Loading, viewModel.Status);
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CanUnload);
        Assert.True(viewModel.UnloadCommand.CanExecute(null));
        Assert.Equal("Loading the local model…", viewModel.StatusText);
        Assert.Contains(nameof(ModelStatusViewModel.StatusText), changed);

        Publish(bus, new ModelStatusChanged(ModelStatus.Ready) { ModelId = "chat" });
        Assert.True(viewModel.IsReady);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("The local model is ready.", viewModel.StatusText);

        Publish(bus, new ModelStatusChanged(ModelStatus.Unloading) { ModelId = "chat" });
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanUnload);
        Assert.Equal("Unloading the local model…", viewModel.StatusText);

        Publish(bus, new ModelStatusChanged(ModelStatus.Failed) { ModelId = "chat", Failure = ModelFailure.RuntimeUnavailable });
        Assert.True(viewModel.IsFailed);
        Assert.Contains("runtime", viewModel.StatusText);

        Publish(bus, new ModelStatusChanged(ModelStatus.NotLoaded));
        Assert.Equal("The local model isn't loaded.", viewModel.StatusText);

        viewModel.Dispose();
        Publish(bus, new ModelStatusChanged(ModelStatus.Ready));
        Assert.Equal(ModelStatus.NotLoaded, viewModel.Status);
    });

    [Fact]
    public void EveryStatusHasWordsAndEveryFailureHasItsOwn()
    {
        foreach (var status in Enum.GetValues<ModelStatus>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ModelStatusText.Describe(status)));
        }

        var failures = Enum.GetValues<ModelFailure>()
            .Select(failure => ModelStatusText.Describe(ModelStatus.Failed, failure)).ToList();
        Assert.Equal(failures.Count, failures.Distinct().Count());
        Assert.All(failures, text => Assert.DoesNotContain("\\", text));
    }

    [Fact]
    public void LoadingWithoutAModelFileAsksForOneAndLoadsNothing() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var viewModel = CreateModelViewModel(lifecycle, out _);

        viewModel.LoadAsync().GetAwaiter().GetResult();
        Assert.Contains("model file", viewModel.Notice);

        viewModel.ModelFilePath = @"models\chat.gguf";
        viewModel.LoadAsync().GetAwaiter().GetResult();
        Assert.Contains("full path", viewModel.Notice);

        viewModel.ModelFilePath = @"C:\models\chat.gguf";
        viewModel.ProjectorFilePath = "mmproj.gguf";
        viewModel.LoadAsync().GetAwaiter().GetResult();
        Assert.Contains("full path", viewModel.Notice);

        Assert.Empty(lifecycle.Loads);
    });

    [Fact]
    public void LoadingGivesTheLifecycleTheFilesTrimmedOfSpacesAndQuotes_AndRemembersThem() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var settings = new InMemorySettingsService();
        var viewModel = CreateModelViewModel(lifecycle, out _, settings);
        viewModel.ModelFilePath = "  \"C:\\models\\chat.gguf\"  ";
        viewModel.ProjectorFilePath = "C:\\models\\chat-mmproj.gguf";

        viewModel.LoadAsync().GetAwaiter().GetResult();

        var files = Assert.Single(lifecycle.Loads);
        Assert.Equal(@"C:\models\chat.gguf", files.ModelPath);
        Assert.Equal(@"C:\models\chat-mmproj.gguf", files.ProjectorPath);
        Assert.Null(files.ChatTemplatePath);
        Assert.Equal("", viewModel.Notice);
        var saved = settings.LoadAsync().GetAwaiter().GetResult().Model;
        Assert.Equal(files.ModelPath, saved.ModelFilePath);
        Assert.Equal(files.ProjectorPath, saved.ProjectorFilePath);
        Assert.Null(saved.ChatTemplateFilePath);

        // A new view model starts from what was saved.
        var second = CreateModelViewModel(new FakeModelLifecycle(), out _, settings);
        second.InitializeAsync().GetAwaiter().GetResult();
        Assert.Equal(files.ModelPath, second.ModelFilePath);
        Assert.Equal(files.ProjectorPath, second.ProjectorFilePath);
        Assert.Equal("", second.ChatTemplateFilePath);
    });

    [Fact]
    public void AFailedLoadIsShownByTheStatusAndNotByANotice() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle { LoadFailure = new ModelHostException(ModelHostErrorCode.ModelLoadFailed) };
        var viewModel = CreateModelViewModel(lifecycle, out _);
        viewModel.ModelFilePath = @"C:\models\chat.gguf";

        viewModel.LoadAsync().GetAwaiter().GetResult();

        Assert.Single(lifecycle.Loads);
        Assert.Equal("", viewModel.Notice);
    });

    [Fact]
    public void TheUnloadCommandUnloadsTheModel() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var viewModel = CreateModelViewModel(lifecycle, out var bus);
        Publish(bus, new ModelStatusChanged(ModelStatus.Ready) { ModelId = "chat" });

        viewModel.UnloadAsync().GetAwaiter().GetResult();

        Assert.Equal(1, lifecycle.Unloads);
    });

    [Fact]
    public void ModelWindowFixtureRendersTheWindowInEachStatus() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var viewModel = CreateModelViewModel(lifecycle, out var bus);
        viewModel.ModelFilePath = @"C:\models\chat.gguf";
        var window = CreateModelWindow(viewModel);
        try
        {
            window.UpdateLayout();
            RenderFixture((FrameworkElement)window.Content, "model-window-not-loaded.png", 1);

            Publish(bus, new ModelStatusChanged(ModelStatus.Loading) { ModelId = "chat" });
            window.UpdateLayout();
            RenderFixture((FrameworkElement)window.Content, "model-window-loading.png", 1);

            Publish(bus, new ModelStatusChanged(ModelStatus.Failed) { ModelId = "chat", Failure = ModelFailure.LoadFailed });
            window.UpdateLayout();
            RenderFixture((FrameworkElement)window.Content, "model-window-failed.png", 1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheWindowShowsTheStatusAndABarOnlyWhileTheModelIsBusy() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var viewModel = CreateModelViewModel(lifecycle, out var bus);
        var window = CreateModelWindow(viewModel);
        try
        {
            var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
            var bar = Assert.IsType<ProgressBar>(window.FindName("BusyBar"));
            var load = Assert.IsType<Button>(window.FindName("LoadButton"));
            var unload = Assert.IsType<Button>(window.FindName("UnloadButton"));
            Assert.Equal("The local model isn't loaded.", status.Text);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            Assert.False(unload.IsEnabled);

            Publish(bus, new ModelStatusChanged(ModelStatus.Loading) { ModelId = "chat" });
            Assert.Equal("Loading the local model…", status.Text);
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.True(bar.IsIndeterminate);
            Assert.True(unload.IsEnabled);

            Publish(bus, new ModelStatusChanged(ModelStatus.Ready) { ModelId = "chat" });
            Assert.Equal("The local model is ready.", status.Text);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);

            // The buttons drive the lifecycle: the model file is empty, so Load only says what is missing.
            Assert.True(load.IsEnabled);
            load.Command.Execute(null);
            Pump();
            Assert.Contains("model file", Assert.IsType<TextBlock>(window.FindName("NoticeText")).Text);
            unload.Command.Execute(null);
            Pump();
            Assert.Equal(1, lifecycle.Unloads);
        }
        finally { window.Close(); }
    });

    private sealed class FakeModelLifecycle : IModelLifecycle
    {
        public ModelStatusChanged Current { get; set; } = new(ModelStatus.NotLoaded);

        public List<ModelFiles> Loads { get; } = [];

        public int Unloads { get; private set; }

        public Exception? LoadFailure { get; init; }

        public Task<ModelInfo> LoadAsync(ModelFiles files, CancellationToken cancellationToken = default)
        {
            Loads.Add(files);
            return LoadFailure is { } failure
                ? Task.FromException<ModelInfo>(failure)
                : Task.FromResult(new ModelInfo(files.DeriveModelId(), 2048));
        }

        public Task UnloadAsync(CancellationToken cancellationToken = default)
        {
            Unloads++;
            return Task.CompletedTask;
        }

        public ModelInfo? Model => null;

        public IAsyncEnumerable<ModelHostReply> GenerateAsync(
            GenerationRequest request, CancellationToken cancellationToken = default) =>
            throw new ModelHostException(ModelHostErrorCode.ModelNotFound, "No model host runs in this test.");
    }

    private sealed class RecordingModelPreview : IModelPreview
    {
        public int Shown { get; private set; }

        public void Show() => Shown++;
    }
}
