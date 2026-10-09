using System.IO;
using System.Text.RegularExpressions;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// Stand-in answers until the orchestrator can produce such content (tools, retrieval, PROJECT_SPEC §5.5). A few fixed
/// questions, the two from the floating conversation's references among them, and "demo" requests get sample answers
/// built from each kind of content, so every presentation can be seen. Nothing is calculated, searched or read: the
/// results, images and files are synthetic. Any other question goes to the local model
/// (<see cref="ModelAnswerProvider"/>), whose answer streams in; without it, it gets no answer.
/// </summary>
/// <remarks>
/// Straightforward arithmetic ("what is 9+10") is worked out by the calculator's tool and drawn as a calculation card, before anything
/// else is tried, when a calculator is registered (<see cref="CalculationAnswers"/>); nothing is registered today, so this does not
/// happen yet and the sample answer below still shows the card.
/// <para/>
/// A request to find the user's own files or pictures ("find the PDF about biology I edited last Tuesday", "show me the last 5
/// screenshots I took") is answered with what Windows Search finds, at once and before the model is asked anything, when file
/// search is wired in (<see cref="FileRequestAnswers"/>); without it the two sample questions below still show sample
/// pictures. <c>demo image</c>, optionally followed by a question, is the developer's test of image requests: it asks for an image
/// file and sends it with the question (or "What's in this image?") to the local model through the real pipeline, the
/// image preprocessor included, and attaches the image to the user's message. The file itself is only read, and only while
/// the Files permission is on.
/// </remarks>
internal sealed partial class DemoAnswerProvider : IAnswerProvider
{
    private const string DemoList = """
        Try one of these to see how each kind of answer is shown:

        - What is 9+10
        - demo photos
        - demo screenshots
        - demo code
        - demo files
        - demo text

        And to find your own files and pictures with Windows Search (these show what it finds, not samples):

        - Show me the last 5 screenshots I took
        - Find the image I took yesterday
        - Find the PDF about biology I edited last Tuesday

        And to see the Searching chip, which shows while the Assistant is busy (press Esc or the chip to cancel):

        - demo searching
        - demo thinking
        - demo working

        And to watch the assistant orb react to sound, in a small window of its own:

        - demo orb

        And to load and unload the local model, and watch its status, in a small window of its own:

        - demo model

        And to change the Assistant's settings, in a window of their own:

        - demo settings

        And to try installing an integration, with a made-up sample package that touches nothing and the real approval panel (it can then be reconnected, turned off and removed in Settings > Integrations):

        - demo integration

        And to try the whole request with the same sample: the Assistant offers it when you ask for it, and once you install it carries on with your request by itself, then goes straight to it the next time:

        - demo integration request

        And to see the question I ask before I do anything that changes something (a sample message to a made-up person: nothing is sent anywhere):

        - demo confirm

        And to see a task with several steps (a made-up one: the panel that shows each step, the button that stops it, the question before the step that changes something, and where a task could not go on; afterwards it is listed in Settings > Activity):

        - demo task
        - demo task fail

        And to try a whole request with made-up parts (a calendar, a messaging app and a brother): I read the calendar, pick out the exams, write the reminder and ask you before I send it:

        - demo reminder

        And to ask the local model about an image file (it needs a model that reads images, loaded with its projector):

        - demo image
        - demo image What does the sign say?

        And to start Visual Intelligence (the dimmed screen, to select something to ask about) or see the image search results window:

        - demo capture
        - demo results

        Anything else is answered by the local model once one is set up there: its answer appears as it is written, and Esc stops it.
        """;

    private const string SampleCode = """
        static int Add(int first, int second)
        {
            // Returns the sum of both numbers.
            return first + second;
        }

        Console.WriteLine(Add(9, 10)); // Prints 19
        """;

    private const string SampleText = """
        # Plain answers
        Most answers are prose like this: open text on the glass, never boxed, read as paragraphs, headings and lists.

        ## What gets its own presentation
        - A calculation's result, in a black card
        - Code, in a code block with a copy button
        - Images, in a gallery three across
        - Files, in a list

        Everything else stays plain text.
        """;

    /// <summary>What the image test asks when no question follows <c>demo image</c>.</summary>
    internal const string DefaultImageQuestion = "What's in this image?";

    // How long the sample busy states last before the answer arrives.
    private static readonly TimeSpan SampleBusyTime = TimeSpan.FromSeconds(5);

    private readonly ITextClipboard _clipboard;
    private readonly TimeProvider _clock;
    private readonly IActivityTracker _activity;
    private readonly TimeSpan _busyTime;
    private readonly IOrbPreview? _orbPreview;
    private readonly IModelPreview? _modelPreview;
    private readonly ModelAnswerProvider? _localModel;
    private readonly IImagePicker? _imagePicker;
    private readonly IImagePreprocessor? _images;
    private readonly ISettingsLauncher? _settingsWindow;
    private readonly IPermissionPolicy? _permissions;
    private readonly FileRequestAnswers? _files;
    private readonly IVisualIntelligenceDemo? _visual;
    private readonly CalculationAnswers? _calculations;
    private readonly ISampleIntegrationDemo? _integrationDemo;
    private readonly IConfirmationDemo? _confirmationDemo;
    private readonly ICalendarReminderDemo? _reminderDemo;
    private readonly IAgentTaskDemo? _taskDemo;

    public DemoAnswerProvider(
        ITextClipboard clipboard, IActivityTracker activity, IOrbPreview? orbPreview = null,
        IModelPreview? modelPreview = null, ModelAnswerProvider? localModel = null, IImagePicker? imagePicker = null,
        IImagePreprocessor? images = null, ISettingsLauncher? settingsWindow = null, IPermissionPolicy? permissions = null,
        FileRequestAnswers? files = null, IVisualIntelligenceDemo? visual = null, CalculationAnswers? calculations = null,
        ISampleIntegrationDemo? integrationDemo = null, IConfirmationDemo? confirmationDemo = null, ICalendarReminderDemo? reminderDemo = null,
        IAgentTaskDemo? taskDemo = null)
        : this(clipboard, TimeProvider.System, activity, orbPreview: orbPreview, modelPreview: modelPreview,
            localModel: localModel, imagePicker: imagePicker, images: images, settingsWindow: settingsWindow,
            permissions: permissions, files: files, visual: visual, calculations: calculations, integrationDemo: integrationDemo,
            confirmationDemo: confirmationDemo, reminderDemo: reminderDemo, taskDemo: taskDemo)
    {
    }

    // Sample files are dated relative to clock's today. Nothing else is measured on it: the busy states wait in real time.
    internal DemoAnswerProvider(
        ITextClipboard clipboard, TimeProvider clock, IActivityTracker? activity = null, TimeSpan? busyTime = null,
        IOrbPreview? orbPreview = null, IModelPreview? modelPreview = null, ModelAnswerProvider? localModel = null,
        IImagePicker? imagePicker = null, IImagePreprocessor? images = null, ISettingsLauncher? settingsWindow = null,
        IPermissionPolicy? permissions = null, FileRequestAnswers? files = null, IVisualIntelligenceDemo? visual = null,
        CalculationAnswers? calculations = null, ISampleIntegrationDemo? integrationDemo = null, IConfirmationDemo? confirmationDemo = null,
        ICalendarReminderDemo? reminderDemo = null, IAgentTaskDemo? taskDemo = null)
    {
        _clipboard = clipboard;
        _clock = clock;
        _activity = activity ?? new ActivityTracker();
        _busyTime = busyTime ?? SampleBusyTime;
        _orbPreview = orbPreview;
        _modelPreview = modelPreview;
        _localModel = localModel;
        _imagePicker = imagePicker;
        _images = images;
        _settingsWindow = settingsWindow;
        _permissions = permissions;
        _files = files;
        _visual = visual;
        _calculations = calculations;
        _integrationDemo = integrationDemo;
        _confirmationDemo = confirmationDemo;
        _reminderDemo = reminderDemo;
        _taskDemo = taskDemo;
    }

    /// <summary>
    /// Whether the developer's samples answer: "demo" and what follows it ("demo task", "demo photos", the made-up results in the bar). They are
    /// on for whoever builds a provider by hand (the tests), and off in the app unless it is started with <see cref="DemosVariable"/> set to 1, so that
    /// nobody who installs the Assistant is shown a made-up conversation, contact or file by typing a word.
    /// </summary>
    public bool DemosEnabled { get; set; } = true;

    /// <summary>The environment variable that turns the samples on in the app: <c>ASSISTANT_DEMOS=1</c>.</summary>
    public const string DemosVariable = "ASSISTANT_DEMOS";

    /// <summary>Whether the app was started with the samples turned on.</summary>
    public static bool DemosRequested => Environment.GetEnvironmentVariable(DemosVariable) == "1";

    // The question as the samples are looked up by; with the samples off, one that begins with "demo" is no sample's.
    private string Key(string question)
    {
        var normalized = Normalize(question);
        return DemosEnabled || !normalized.StartsWith("demo", StringComparison.Ordinal) ? normalized : "\u0001" + normalized;
    }

    /// <inheritdoc/>
    public MessageViewModel? Answer(string question) => Sample(Key(question))?.Invoke();

    /// <inheritdoc/>
    public void Resume(Guid conversationId, IReadOnlyList<MessageViewModel> earlier) => _localModel?.Resume(conversationId, earlier);

    /// <inheritdoc/>
    public void ReleaseContext(Guid conversationId, Guid contextItemId) => _localModel?.ReleaseContext(conversationId, contextItemId);

    /// <inheritdoc/>
    /// <remarks>Only the local model sets a request aside (for an integration it needs), so it is the one that goes back to it.</remarks>
    public Task StreamContinuationAsync(
        Guid conversationId, PendingContinuation continuation, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        _localModel?.StreamContinuationAsync(conversationId, continuation, show, cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// The sample questions are answered as before; any other goes to the local model, whose answer streams in.
    /// </remarks>
    public Task StreamAnswerAsync(string question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        StreamAnswerAsync(null, question, null, show, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The local model remembers the conversation, so a follow-up to one of its answers is answered in context.</remarks>
    public Task StreamAnswerAsync(
        Guid conversationId, string question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        StreamAnswerAsync((Guid?)conversationId, question, null, show, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The image test attaches the image it asked about to the user's message.</remarks>
    public Task StreamAnswerAsync(
        Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        return StreamAnswerAsync((Guid?)conversationId, question.Text, question, show, cancellationToken);
    }

    private async Task StreamAnswerAsync(
        Guid? conversationId, string question, MessageViewModel? asked, Action<MessageViewModel> show,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(show);

        // The test of the question that is asked before anything is changed (step 115): the real question, in this answer, over a made-up person.
        if (_confirmationDemo is not null && Key(question) is "demo confirm" or "demo confirmation")
        {
            await _confirmationDemo.RunAsync(conversationId, show, cancellationToken).ConfigureAwait(true);
            return;
        }

        // The test of a task with several steps (step 117): the real loop, panel, question and log, over a made-up plan and made-up tools.
        if (_taskDemo is not null && Key(question) is "demo task" or "demo tasks" or "demo task fail")
        {
            await _taskDemo.RunAsync(conversationId, Key(question) == "demo task fail", show, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (DemosEnabled && ImageQuestion(question) is { } imageQuestion)
        {
            await AskAboutImageAsync(conversationId, imageQuestion, asked, show, cancellationToken).ConfigureAwait(true);
            return;
        }

        // A sum is worked out by the calculator, at once and by fixed rules, when there is one: it is no question for the model, or for
        // the search. A question with something attached is about that, whatever its words.
        if (_calculations is not null && asked is not { HasAttachments: true }
            && await _calculations.TryAnswerAsync(question, conversationId, cancellationToken).ConfigureAwait(true) is { } calculated)
        {
            show(calculated);
            return;
        }

        // Once the conversation has a file in it (one that was found for it, or attached to it), what the user says is for the model: it
        // sees the whole conversation, the lists of files in it and which one was talked about, and works out what "it" or "the first
        // one" means, and whether to look for a file or read one, with the tools it is given (PROJECT_SPEC §4.8). Before that the first
        // request to find files is answered at once from what Windows Search finds, with no model asked.
        var fileContext = conversationId is { } chat && (_localModel?.HasFileContext(chat) == true || _localModel?.HasScreenContext(chat) == true);

        // A request to find the user's own files is answered by what Windows Search finds, at once. A question that has an image or a
        // document attached is about that, whatever its words.
        if (!fileContext && _files is not null && asked is not { HasAttachments: true })
        {
            // "Find the milestone doc, what is it about?" is both: the file is found, then, when exactly one the Assistant can read
            // was found, the rest is asked about it, as if the user had attached it. So is "summarize my milestone doc", which
            // names the file it asks about.
            string? search = null;
            string? about = null;
            if (_files.IsFileRequest(question, conversationId))
            {
                search = question;
                if (DocumentCue.TrySplit(question, out var findPart, out var askPart) && _files.IsFileRequest(findPart, conversationId))
                {
                    (search, about) = (findPart, askPart);
                }
            }
            else if (DocumentCue.FindOfNamed(question) is { } named && _files.IsFileRequest(named, conversationId))
            {
                (search, about) = (named, question);
            }

            if (search is not null)
            {
                var found = await _files.AnswerAsync(search, conversationId, cancellationToken).ConfigureAwait(true);
                show(found);
                RememberFound(conversationId, search, found);
                if (about is not null && _localModel is not null && FileActions.SingleDocumentIn(found) is { } document)
                {
                    await _localModel.StreamAnswerAsync(
                        conversationId, new MessageViewModel(MessageRole.User, about, null, document), show, cancellationToken)
                        .ConfigureAwait(true);
                }

                return;
            }
        }

        // A request that may be about files without being clearly one ("i said milestone three doc. FIND IT", "milestone 3 doc") is
        // looked for, and is a request to find files only when something is found; otherwise it is for the model.
        if (!fileContext && _files is not null && asked is not { HasAttachments: true } && Sample(Key(question)) is null
            && BusyKind(Key(question)) is null && _files.IsLikelyFileRequest(question)
            && await _files.TryAnswerAsync(question, conversationId, cancellationToken).ConfigureAwait(true) is { } likely)
        {
            show(likely);
            RememberFound(conversationId, question, likely);
            return;
        }

        // A question about an attached document goes to the model, whatever its words: it is not one of the samples.
        var normalized = Key(question);
        var aboutDocument = asked is { Document: not null } or { TextAttachments.Count: > 0 };
        if (_localModel is not null && (aboutDocument || (Sample(normalized) is null && BusyKind(normalized) is null)))
        {
            if (asked is { HasAttachments: true } && conversationId is { } attachedTo)
            {
                await _localModel.StreamAnswerAsync(attachedTo, asked, show, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await (conversationId is { } id
                        ? _localModel.StreamAnswerAsync(id, question, show, cancellationToken)
                        : _localModel.StreamAnswerAsync(question, show, cancellationToken))
                    .ConfigureAwait(true);
            }
        }
        else if (await AnswerAsync(question, cancellationToken).ConfigureAwait(true) is { } answer)
        {
            show(answer);
        }
    }

    // The files found for a request are part of what the model remembers of the conversation, as its own call of the tool that finds
    // files and what it returned, so that what is said next ("the first one", "3 not 4") is understood in it.
    private void RememberFound(Guid? conversationId, string search, MessageViewModel answer)
    {
        if (_localModel is not null && conversationId is { } id && _files?.ToolExchange(id, search, answer) is { } exchange)
        {
            _localModel.RememberFound(id, search, exchange.Call, exchange.Result, answer.Text);
        }
    }

    // The question of an image test ("demo image", then the question if one follows), or null for any other question.
    internal static string? ImageQuestion(string question)
    {
        var match = ImageCommand().Match(question);
        if (!match.Success)
        {
            return null;
        }

        // "demo image: what is this?" and "demo image?" read as they are meant.
        var asked = question[match.Length..].Trim().TrimStart(':', ',', '-').Trim();
        return asked.Trim('?', '.', '!').Length > 0 ? asked : DefaultImageQuestion;
    }

    // The image test: an image file the user picks, sent with the question through the whole pipeline.
    private async Task AskAboutImageAsync(
        Guid? conversationId, string question, MessageViewModel? asked, Action<MessageViewModel> show,
        CancellationToken cancellationToken)
    {
        if (_localModel is null || _imagePicker is null || _images is null || _permissions is null)
        {
            show(Prose("The image test is not available here."));
            return;
        }

        // Reading a file the user picks needs the Files permission (Settings, Permissions). Nothing is opened without it,
        // not even the file dialog.
        if (!(await _permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(true)).IsAllowed)
        {
            show(Prose("Files are turned off in Settings, under Permissions, so nothing was read. Turn Files on there to ask about an image."));
            return;
        }

        if (await _imagePicker.PickAsync(cancellationToken).ConfigureAwait(true) is not { } path)
        {
            show(Prose("No image was chosen, so nothing was asked."));
            return;
        }

        byte[] image;
        try
        {
            // Prepared here only to tell at once whether it is an image at all; the pipeline prepares its own copy.
            image = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
            await _images.PrepareAsync(image, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            show(Prose("That file couldn't be opened."));
            return;
        }
        catch (ImagePreprocessingException)
        {
            show(Prose("That file isn't an image the Assistant can read."));
            return;
        }

        var name = Path.GetFileName(path);
        var item = new ContextItem(Guid.NewGuid(), ContextItemType.Image, name) { ImageData = image };
        // The image belongs to the user's question: it is drawn above their bubble, and History finds the conversation's
        // latest attached image there. Asked without a message to attach it to, it leads the answer instead.
        var shown = new ImageItem(name, path);
        MessageContent? lead = null;
        if (asked is not null)
        {
            asked.Attach(shown);
        }
        else
        {
            lead = new ImageCollection([shown]);
        }

        await _localModel.StreamAnswerAsync(conversationId, question, [item], lead, show, cancellationToken)
            .ConfigureAwait(true);
    }

    // The sample answer to a normalized question, made only when it is asked for: some open a window.
    private Func<MessageViewModel>? Sample(string normalized) => normalized switch
    {
        "what is 9+10" or "whats 9+10" or "9+10" or "demo calculator" => Calculation,
        "find the image i took yesterday" or "find the images i took yesterday" or "demo photos" => Photos,
        "show me the last 5 screenshots i took" or "demo screenshots" => Screenshots,
        "demo code" or "show me some code" => Code,
        "demo files" or "find my budget files" => Files,
        "demo text" => () => Prose(SampleText),
        "demo orb" => Orb,
        "demo model" => Model,
        "demo settings" => Settings,
        "demo integration" or "demo integrations" or "demo integration request" or "demo integration requests" => IntegrationUnavailable,
        "demo confirm" or "demo confirmation" => ConfirmationUnavailable,
        "demo task" or "demo tasks" or "demo task fail" => TaskUnavailable,
        "demo reminder" or "demo reminders" or "demo reminder off" => ReminderUnavailable,
        "demo capture" => Capture,
        "demo results" => Results,
        "demo" => () => Prose(DemoList),
        _ => null,
    };

    /// <inheritdoc/>
    public async Task<MessageViewModel?> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        // The offer of the sample integration takes a moment (its bundle is made and its plan worked out), so it is made here and not in the samples above.
        if (Key(question) is "demo integration" or "demo integrations" && _integrationDemo is not null)
        {
            return await _integrationDemo.OfferAsync(cancellationToken).ConfigureAwait(true);
        }

        // The request flow (step 110): this only turns the sample on for the request that is asked next; the request itself goes through the Assistant as any other.
        if (Key(question) is "demo integration request" or "demo integration requests" && _integrationDemo is not null)
        {
            return await _integrationDemo.StartRequestDemoAsync(cancellationToken).ConfigureAwait(true);
        }

        // The whole request with made-up parts (step 116): this only turns the demo on (or off); the request itself goes through the Assistant as any other.
        if (_reminderDemo is not null && Key(question) is "demo reminder" or "demo reminders")
        {
            return await _reminderDemo.StartAsync(cancellationToken).ConfigureAwait(true);
        }

        if (_reminderDemo is not null && Key(question) is "demo reminder off")
        {
            return _reminderDemo.Stop();
        }

        if (BusyKind(Key(question)) is not { } kind)
        {
            return Answer(question);
        }

        // A sample of an operation that takes a while, reported as a real search, model call or tool would be: the chip
        // shows for the wait, and cancelling it, or the conversation, stops it.
        using var activity = _activity.Begin(kind, cancellationToken: cancellationToken);
        try
        {
            await Task.Delay(_busyTime, activity.CancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Prose("Stopped. Nothing was found.");
        }

        return Prose("That was a sample of a busy state: nothing was searched. Real searches, the model and tools show the same chip while they run.");
    }

    private static ActivityKind? BusyKind(string normalized) => normalized switch
    {
        "demo searching" or "demo search" => ActivityKind.WebSearch,
        "demo thinking" => ActivityKind.Model,
        "demo working" => ActivityKind.Tool,
        _ => null,
    };

    // Case, apostrophes, spacing around a plus sign and closing punctuation do not matter.
    internal static string Normalize(string question)
    {
        var text = question.Trim().ToLowerInvariant().Replace("'", "", StringComparison.Ordinal)
            .Replace("’", "", StringComparison.Ordinal);
        text = SpacesAroundPlus().Replace(text, "+");
        text = Spaces().Replace(text, " ");
        return text.TrimEnd('?', '.', '!', ' ');
    }

    private MessageViewModel Orb()
    {
        _orbPreview?.Show();
        return Prose(_orbPreview is null
            ? "The orb preview is not available here."
            : "Opened the orb preview in its own window. Pick a state and a sound to watch the orb: invented speech, a slider, or your microphone (which listens only while you are in that window).");
    }

    private MessageViewModel Model()
    {
        _modelPreview?.Show();
        return Prose(_modelPreview is null
            ? "The model window is not available here."
            : "Opened the model window. Choose a .gguf model file (and a projector file if the model reads images), then press Load to watch the status change, and Unload to free the memory again.");
    }

    private MessageViewModel Settings()
    {
        _settingsWindow?.Show();
        return Prose(_settingsWindow is null
            ? "The settings window is not available here."
            : "Opened the settings window. Every change there is saved as you make it.");
    }

    private static MessageViewModel IntegrationUnavailable() => Prose("The sample integration is not available here.");

    private static MessageViewModel ConfirmationUnavailable() => Prose("The confirmation test is not available here.");

    private static MessageViewModel ReminderUnavailable() => Prose("The reminder demo is not available here.");

    private static MessageViewModel TaskUnavailable() => Prose("The task demo is not available here.");

    private MessageViewModel Capture()
    {
        _visual?.StartCapture();
        return Prose(_visual is null
            ? "Visual Intelligence is not available here."
            : "Started Visual Intelligence: drag over what you want to ask about, then choose Ask Assistant, Image Search or Copy. Esc cancels.");
    }

    private MessageViewModel Results()
    {
        _visual?.ShowSampleResults();
        return Prose(_visual is null
            ? "The image search results window is not available here."
            : "Opened the image search results window with made-up samples: no search was made and nothing was sent.");
    }

    private static MessageViewModel Prose(string text) => new(MessageRole.Assistant, text);

    private MessageViewModel Calculation()
    {
        var answer = Prose("9 + 10 is 19.");
        answer.Content.Add(new CalculationResult("9 + 10", "19", new CopyTextCommand(_clipboard, "19")));
        return answer;
    }

    private static MessageViewModel Photos()
    {
        var answer = Prose("I found 4 photos from yesterday.");
        answer.Content.Add(new ImageCollection(DemoImages.Photos()));
        return answer;
    }

    private static MessageViewModel Screenshots()
    {
        var answer = Prose("Here are 5 sample screenshots. The screenshots the Assistant finds on this PC are shown this way.");
        answer.Content.Add(new ImageCollection(DemoImages.Screenshots(5)));
        return answer;
    }

    private MessageViewModel Code()
    {
        var answer = Prose("Here's a C# method that adds two numbers, and a line that calls it:");
        // The sample is copied with \n line breaks, as the block shows it, whatever line endings the source file has.
        var code = SampleCode.ReplaceLineEndings("\n");
        answer.Content.Add(new CodeContent(code, "C#", new CopyTextCommand(_clipboard, code)));
        answer.Content.Add(new TextContent("Calling Add(9, 10) prints 19."));
        return answer;
    }

    private MessageViewModel Files()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var now = _clock.GetLocalNow();
        var answer = Prose("Here are some sample file results. The files the Assistant finds on this PC are listed this way.");
        answer.Content.Add(new FileCollection(
        [
            new FileItem(SearchResultItemType.File, "Budget 2026.xlsx", Path.Combine(documents, "Budget 2026.xlsx"),
                now.AddHours(-2), "Groceries 420 · Rent 1,450 · Savings 600 · Travel 250", _clock),
            new FileItem(SearchResultItemType.File, "Budget notes.docx", Path.Combine(documents, "Finance", "Budget notes.docx"),
                now.AddDays(-1), "Keep the monthly budget under 2,800 and move anything left over into savings.", _clock),
            new FileItem(SearchResultItemType.File, "Trip budget.pdf", Path.Combine(downloads, "Trip budget.pdf"),
                now.AddDays(-40), clock: _clock),
            new FileItem(SearchResultItemType.Folder, "Finance", Path.Combine(documents, "Finance"), now.AddDays(-3), clock: _clock),
        ]));
        return answer;
    }

    [GeneratedRegex(@"^\s*demo\s+image\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageCommand();

    [GeneratedRegex(@"\s*\+\s*")]
    private static partial Regex SpacesAroundPlus();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
