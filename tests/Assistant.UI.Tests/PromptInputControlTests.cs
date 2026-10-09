using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Voice;
using Assistant.Windows.Audio;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static readonly Lazy<Dispatcher> UiDispatcher = new(() =>
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)), "WPF dispatcher did not start.");
        return dispatcher!;
    });

    [Fact]
    public void DefaultTemplateLoadsWithoutApplicationResources() => RunSta(() =>
    {
        var input = CreateInput();
        Assert.Equal(91, input.ActualHeight);
        Assert.False(Editor(input).AcceptsReturn);
        Assert.Equal(Visibility.Visible, Part<TextBlock>(input, "Placeholder").Visibility);
        var microphone = Part<Button>(input, "Microphone");
        Assert.False(microphone.IsEnabled);
        Assert.Equal(1, microphone.Opacity);
        Assert.NotEmpty(Descendants<System.Windows.Shapes.Path>(microphone));
    });

    [Fact]
    public void EditorBindingUpdatesHostAndEscapeClearsWithoutBreakingBinding() => RunSta(() =>
    {
        var viewModel = CreateBarModel();
        var input = CreateInput();
        input.DataContext = viewModel;
        input.SetBinding(PromptInputControl.TextProperty, new Binding(nameof(viewModel.Query)));
        var editor = Editor(input);
        editor.Text = "sample draft";
        Pump();
        Assert.Equal("sample draft", viewModel.Query);
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(input, "Placeholder").Visibility);
        Assert.False(viewModel.HandleEscape());
        Pump();
        Assert.Equal("", editor.Text);
        Assert.Equal(Visibility.Visible, Part<TextBlock>(input, "Placeholder").Visibility);
        Assert.True(viewModel.HandleEscape());
        editor.Text = "another draft";
        Assert.Equal("another draft", viewModel.Query);
    });

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n\t")]
    public void BlankDraftDoesNotSubmit(string text) => RunSta(() =>
    {
        var command = new RecordingCommand();
        var input = CreateInput();
        input.Text = text;
        input.SubmitCommand = command;
        Assert.False(input.TrySubmit());
        Assert.Equal(0, command.Count);
    });

    [Fact]
    public void EnterExecutesOnceWithLatestUntrimmedTextAndPreservesDraft() => RunSta(() =>
    {
        var command = new RecordingCommand();
        var input = CreateInput();
        input.SubmitCommand = command;
        Editor(input).Text = "  sample draft  ";
        using var source = new HwndSource(new HwndSourceParameters("Prompt input test") { Width = 1, Height = 1, WindowStyle = 0 });
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Editor(input).RaiseEvent(key);
        Assert.True(key.Handled);
        Assert.Equal(1, command.Count);
        Assert.Equal("  sample draft  ", command.Parameter);
        Assert.Equal("  sample draft  ", input.Text);
    });

    [Fact]
    public void DisabledMissingAndRejectedCommandsKeepDraft() => RunSta(() =>
    {
        var input = CreateInput();
        input.Text = "draft";
        Assert.False(input.TrySubmit());
        var command = new RecordingCommand { Enabled = false };
        input.SubmitCommand = command;
        Assert.False(input.TrySubmit());
        command.Enabled = true;
        input.IsEnabled = false;
        Assert.False(input.TrySubmit());
        Assert.Equal(0, command.Count);
        Assert.Equal("draft", input.Text);
        input.IsEnabled = true;
        Assert.True(input.TrySubmit());
    });

    [Fact]
    public void CommandCanUseCustomParameterAndClearBoundDraft() => RunSta(() =>
    {
        var viewModel = CreateBarModel();
        viewModel.Query = "draft";
        var input = CreateInput();
        input.DataContext = viewModel;
        input.SetBinding(PromptInputControl.TextProperty, new Binding(nameof(viewModel.Query)));
        var parameter = new object();
        var command = new RecordingCommand { OnExecute = () => viewModel.Query = "" };
        input.SubmitCommand = command;
        input.SubmitCommandParameter = parameter;
        Assert.True(input.TrySubmit());
        Assert.Same(parameter, command.Parameter);
        Assert.Same(parameter, command.CanExecuteParameter);
        Pump();
        Assert.Equal("", Editor(input).Text);
        Assert.True(BindingOperations.IsDataBound(input, PromptInputControl.TextProperty));
    });

    [Theory]
    [InlineData(ModifierKeys.None, false, false, true, 1)]
    [InlineData(ModifierKeys.Shift, false, false, true, 1)]
    [InlineData(ModifierKeys.Shift, true, false, false, 0)]
    [InlineData(ModifierKeys.None, true, false, true, 1)]
    [InlineData(ModifierKeys.None, false, true, true, 0)]
    [InlineData(ModifierKeys.Control, true, false, true, 1)]
    [InlineData(ModifierKeys.Control, false, false, true, 1)]
    [InlineData(ModifierKeys.Alt, true, false, true, 0)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, true, false, true, 0)]
    public void EnterHonorsModifiersAndRepeat(ModifierKeys modifiers, bool multiline, bool repeat, bool handled, int submissions) => RunSta(() =>
    {
        var input = CreateInput();
        var command = new RecordingCommand();
        input.Text = "draft";
        input.SubmitCommand = command;
        input.AllowMultiline = multiline;
        Assert.Equal(handled, input.HandleEnter(modifiers, repeat));
        Assert.Equal(submissions, command.Count);
        Assert.Equal(multiline, Editor(input).AcceptsReturn);
    });

    [Fact]
    public void ExpandedEditorGrowsThenScrollsAndUsesNativeNewlineUndo() => RunSta(() =>
    {
        var input = CreateInput(expanded: true);
        var editor = Editor(input);
        Assert.True(input.AllowMultiline);
        Assert.Equal(TextWrapping.Wrap, editor.TextWrapping);
        var emptyHeight = input.ActualHeight;
        editor.Text = "line one\r\nline two\r\nline three\r\nline four";
        Layout(input);
        Assert.True(input.ActualHeight > emptyHeight);
        editor.Text = string.Join("\r\n", Enumerable.Repeat("line", 30));
        Layout(input);
        Assert.True(editor.ExtentHeight > editor.ViewportHeight);
        Assert.True(input.ActualHeight < 200);
        var window = new Window
        {
            Content = input, Width = 540, Height = 300, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            window.Activate();
            Assert.True(input.FocusInput());
            Pump();
            Assert.True(input.IsKeyboardFocusWithin);
            editor.Text = "before after";
            editor.Select(7, 5);
            EditingCommands.EnterLineBreak.Execute(null, editor);
            Assert.Equal("before \r\n", editor.Text);
            editor.Undo();
            Assert.Equal("before after", editor.Text);
            editor.Text = "A sample prompt that wraps across the expanded input.\r\nShift+Enter adds another line.";
            input.VerticalAlignment = VerticalAlignment.Top;
            window.UpdateLayout();
            RenderFixture(input, "expanded-input.png");
            input.AllowMultiline = false;
            Assert.False(editor.AcceptsReturn);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MicrophoneSupportsContentParameterEnablementAndHiding() => RunSta(() =>
    {
        var input = CreateInput();
        var command = new RecordingCommand { Enabled = false };
        var content = new TextBlock { Text = "Voice" };
        input.MicrophoneContent = content;
        input.MicrophoneCommandParameter = "voice-action";
        input.MicrophoneCommand = command;
        var button = Part<Button>(input, "Microphone");
        Assert.False(button.IsEnabled);
        command.Enabled = true;
        command.RaiseCanExecuteChanged();
        Pump();
        Assert.True(button.IsEnabled);
        ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
        Pump();
        Assert.Equal(1, command.Count);
        Assert.Equal("voice-action", command.Parameter);
        Assert.Same(content, Part<ContentPresenter>(input, "MicrophoneSlot").Content);
        input.ShowMicrophone = false;
        Assert.Equal(Visibility.Collapsed, button.Visibility);
    });

    [Fact]
    public void RoutedSubmitAndHostCommandsUseEditorAsTarget() => RunSta(() =>
    {
        var host = new Grid();
        var input = CreateInput();
        host.Children.Add(input);
        var hostCommand = new RoutedCommand();
        object? received = null;
        host.CommandBindings.Add(new CommandBinding(hostCommand,
            (_, e) => received = e.Parameter,
            (_, e) => e.CanExecute = true));
        input.SubmitCommand = hostCommand;
        input.Text = "draft";
        Assert.True(PromptInputControl.Submit.CanExecute(null, input));
        PromptInputControl.Submit.Execute(null, input);
        Assert.Equal("draft", received);
    });

    [Fact]
    public void ImeCompositionEnterDoesNotSubmit() => RunSta(() =>
    {
        var input = CreateInput();
        var command = new RecordingCommand();
        input.SubmitCommand = command;
        input.Text = "draft";
        var editor = Editor(input);
        var composition = new TextComposition(InputManager.Current, editor, "");
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent,
        });
        Assert.False(input.TrySubmit());
        using var source = new HwndSource(new HwndSourceParameters("Prompt IME test") { Width = 1, Height = 1, WindowStyle = 0 });
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.RaiseEvent(key);
        Assert.False(key.Handled);
        Assert.Equal(0, command.Count);
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
        });
        Assert.True(input.TrySubmit());
    });

    [Fact]
    public void ExistingPillLoadsFocusesAndRetainsEscapeBehavior() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var viewModel = CreateBarModel();
        var window = new AssistantWindow(viewModel, CreateConversationModel(), new FakeBackdropFactory(), new FakePlacement())
        {
            Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            Pump();
            var input = Assert.IsType<PromptInputControl>(window.FindName("PromptInput"));
            Assert.Equal(520, input.ActualWidth);
            Assert.Equal(91, input.ActualHeight);
            Assert.True(input.IsKeyboardFocusWithin);
            var editor = Editor(input);
            Assert.Equal(36, editor.TranslatePoint(new Point(), input).X, 2);
            var glyph = Assert.Single(Descendants<System.Windows.Shapes.Path>(Part<Button>(input, "Microphone")));
            Assert.Equal(466.75, glyph.TranslatePoint(new Point(), input).X, 2);
            RenderGlass(window, "compact-input.png");
            editor.Text = "draft";
            var source = PresentationSource.FromVisual(window)!;
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Assert.Equal("", viewModel.Query);
            Assert.True(window.IsVisible);
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            WaitUntil(() => !window.IsVisible, "Escape on an empty draft did not hide the overlay.");
            var handle = new WindowInteropHelper(window).Handle;
            window.ShowAndFocus();
            Assert.True(window.IsVisible);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); app.Resources.MergedDictionaries.Clear(); }
    });

    private static PromptInputControl CreateInput(bool expanded = false)
    {
        var input = new PromptInputControl { Width = 520 };
        input.BeginInit();
        if (expanded)
        {
            var resources = new ResourceDictionary
            {
                Source = new Uri("/Assistant.UI;component/Themes/Controls/PromptInput.xaml", UriKind.Relative),
            };
            input.Style = (Style)resources["PromptInput.Expanded"];
        }

        input.EndInit();
        Layout(input);
        return input;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(520, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static TextBox Editor(PromptInputControl input) => Part<TextBox>(input, "PART_Editor");
    private static T Part<T>(PromptInputControl input, string name) where T : class => Assert.IsType<T>(input.Template.FindName(name, input));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    // Opt-in render of synthetic test fixtures only; never captures the screen or a user's content. A scale of 2
    // matches the 2x reference images.
    private static void RenderFixture(FrameworkElement element, string name, double scale = 1, Rect? region = null)
    {
        var directory = Environment.GetEnvironmentVariable("ASSISTANT_UI_RENDER_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(element, scale, region)));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    // The alpha of each pixel of an element drawn on its own, by row and then column.
    private static byte[,] RenderAlpha(FrameworkElement element)
    {
        var bitmap = Render(element);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var alpha = new byte[bitmap.PixelHeight, bitmap.PixelWidth];
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                alpha[y, x] = pixels[(y * bitmap.PixelWidth + x) * 4 + 3];
            }
        }

        return alpha;
    }

    // Draws an element as laid out, without its surroundings, into a bitmap with premultiplied alpha. A region draws
    // only that part of it, given in the element's own coordinates.
    private static RenderTargetBitmap Render(FrameworkElement element, double scale = 1, Rect? region = null)
    {
        var bounds = region ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale),
            (int)Math.Ceiling(bounds.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var target = new Rect(0, 0, bounds.Width, bounds.Height);
            var offset = (Point)VisualTreeHelper.GetOffset(element);
            context.DrawRectangle(new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X + bounds.X, offset.Y + bounds.Y, bounds.Width, bounds.Height),
            }, null, target);
        }
        bitmap.Render(drawing);
        return bitmap;
    }

    // The Assistant's glass as drawn now, with the contents on it and without the room around it for its shadow, as
    // the references show it: the same picture whether it is the pill or the panel.
    private static void RenderGlass(AssistantWindow window, string name, double scale = 1) =>
        RenderFixture(GridNamed(window, "SurfaceHost"), name, scale, GlassRegion(window));

    private static Rect GlassRegion(AssistantWindow window)
    {
        var glass = GridNamed(window, "SurfaceArea");
        return glass.TransformToAncestor(GridNamed(window, "SurfaceHost")).TransformBounds(new Rect(glass.RenderSize));
    }

    private static Grid GridNamed(AssistantWindow window, string name) => Assert.IsType<Grid>(window.FindName(name));

    private static void RunSta(Action action)
    {
        UiDispatcher.Value.Invoke(action, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(20));
    }

    private static SearchOrAskViewModel CreateBarModel(
        FakeMicrophone? microphone = null, LauncherViewModel? launcher = null, SearchResultsViewModel? results = null,
        ActivityViewModel? activity = null, Assistant.Core.QuickSearch.Routing.IQueryRouter? router = null, IVoiceInput? input = null,
        ISpokenAnswers? speech = null) =>
        new(new VoiceInputViewModel(microphone ?? new FakeMicrophone(), input, speech), launcher, results, activity, router);

    private static ConversationViewModel CreateConversationModel(
        FakeMicrophone? microphone = null, IAnswerProvider? answers = null, ActivityViewModel? activity = null, IVoiceInput? input = null,
        ISpokenAnswers? speech = null) =>
        new(new VoiceInputViewModel(microphone ?? new FakeMicrophone(), input, speech), answers ?? new FakeAnswers(), activity: activity, speech: speech);

    private sealed class RecordingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool Enabled { get; set; } = true;
        public int Count { get; private set; }
        public object? Parameter { get; private set; }
        public object? CanExecuteParameter { get; private set; }
        public Action? OnExecute { get; init; }
        public bool CanExecute(object? parameter) { CanExecuteParameter = parameter; return Enabled; }
        public void Execute(object? parameter) { Count++; Parameter = parameter; OnExecute?.Invoke(); }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeBackdropFactory : IWindowBackdropFactory
    {
        // The window's own backdrop is the first: the pill's glass. The launcher's panel gets the second.
        public List<FakeBackdrop> All { get; } = [];
        public FakeBackdrop? Created => All.FirstOrDefault();
        public FakeBackdrop? Launcher => All.Skip(1).FirstOrDefault();

        public IWindowBackdrop Create(nint window)
        {
            var backdrop = new FakeBackdrop();
            All.Add(backdrop);
            return backdrop;
        }
    }

    private sealed class FakeBackdrop : IWindowBackdrop
    {
        public nint Handle => 0;
        public bool IsBlurred => false;
        public BackdropRegion Region { get; private set; }
        public double Opacity { get; private set; } = 1;
        public event EventHandler? IsBlurredChanged { add { } remove { } }
        public void SetRegion(BackdropRegion region) => Region = region;
        public void SetOpacity(double opacity) => Opacity = opacity;
        public void Dispose() { }
    }

    // Opens nothing: sessions report the level a test sets, and a test can fail them as the real meter would.
    private sealed class FakeMicrophone : IMicrophoneLevelMeter
    {
        public List<FakeMicrophoneSession> Sessions { get; } = [];
        public FakeMicrophoneSession? Current => Sessions.LastOrDefault(session => !session.IsDisposed);

        public IMicrophoneLevelSession Start(Action<MicrophoneFailure> failed)
        {
            var session = new FakeMicrophoneSession(failed);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeMicrophoneSession(Action<MicrophoneFailure> failed) : IMicrophoneLevelSession
    {
        public double Level { get; set; }
        public bool IsDisposed { get; private set; }
        public void Fail(MicrophoneFailure failure) => failed(failure);
        public void Dispose() => IsDisposed = true;
    }

    // Records placements without moving anything, so test windows stay where the test put them.
    private sealed class FakePlacement : IWindowPlacementService
    {
        public List<(nint Window, OverlayLayout Layout)> Calls { get; } = [];
        public Action? OnPlace { get; set; }

        public List<(nint Window, OverlayLayout Layout, ScreenPoint Point)> PointCalls { get; } = [];
        public ScreenPoint? AnchorTop { get; set; }

        public WindowPlacement? PlaceOnActiveMonitor(nint window, OverlayLayout layout)
        {
            Calls.Add((window, layout));
            OnPlace?.Invoke();
            return null;
        }

        public ScreenPoint? GetAnchorTop(nint window, OverlayLayout layout) => AnchorTop;

        // Reports what a placement of the layout would do for a test, or else that the point is fine where it is.
        public Func<OverlayLayout, ScreenPoint, ScreenPoint?>? Fit { get; set; }
        public ScreenPoint? FitAnchorTop(OverlayLayout layout, ScreenPoint point) => Fit is { } fit ? fit(layout, point) : point;

        public WindowPlacement? PlaceAnchorTopAt(nint window, OverlayLayout layout, ScreenPoint point)
        {
            PointCalls.Add((window, layout, point));
            return null;
        }

        // The window in front, as the placement service would note it for a way in that opens beside it, and where it would put the panel.
        public NearWindowTarget? Foreground { get; set; }
        public List<int> DescribeCalls { get; } = [];
        public bool Throws { get; set; }
        public NearWindowTarget? DescribeForegroundWindow(int excludeProcessId)
        {
            DescribeCalls.Add(excludeProcessId);
            return Throws ? throw new InvalidOperationException("Windows would not say.") : Foreground;
        }

        public List<(NearWindowTarget Target, OverlayLayout Layout)> NearCalls { get; } = [];
        public ScreenPoint? Near { get; set; }

        public ScreenPoint? FindAnchorTopNear(NearWindowTarget target, OverlayLayout layout)
        {
            NearCalls.Add((target, layout));
            return Near;
        }

        public List<(nint Window, double Width, double Height, double Margin)> CenterCalls { get; } = [];

        public WindowPlacement? PlaceCenteredOnActiveMonitor(nint window, double width, double height, double margin)
        {
            CenterCalls.Add((window, width, height, margin));
            return null;
        }
    }
}
