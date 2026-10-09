using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.Memory;
using Assistant.Core.Settings;
using Assistant.Core.Tools;

namespace Assistant.Core.Orchestration;

/// <summary>
/// Builds the model's prompt (PROJECT_SPEC §5.5). Every prompt the Assistant sends is put together here, so no other
/// code decides how instructions, earlier turns and the user's context are laid out.
/// </summary>
/// <remarks>
/// <para>
/// The layout is a function of the conversation alone, so a follow-up's prompt starts with exactly the earlier one and
/// the engine can reuse what it has already read. The system message is the instructions followed by
/// <see cref="AssistantInstructions.UntrustedContextGuidance"/>, and by <see cref="AssistantInstructions.FileContextGuidance"/>
/// when the conversation carries the text of an attached file (and <see cref="AssistantInstructions.FileNotesGuidance"/> when it
/// carries notes taken on files too long to read at once). Each user message is its context, each piece
/// wrapped as untrusted (<see cref="UntrustedContext"/>), followed by the text the user typed, which comes last
/// because it is what the model must act on. A message's context stays with it in later turns, as far as its content is
/// still in memory.
/// </para>
/// <para>
/// A screenshot or an attached image goes to the model as an image when it can read images, and otherwise as its
/// recognized text, or not at all; only the newest message's images are sent. They are sent as they were captured:
/// making them ready for the model is the orchestrator's next step (<see cref="IImagePreprocessor"/>). Anything left out
/// or changed is reported in <see cref="BuiltPrompt.Notices"/>.
/// </para>
/// <para>
/// Given the user's <see cref="ContextLimitSettings"/>, the builder also fits the conversation into the model's
/// context window before it lays it out (<see cref="ContextBudgeter"/>): what does not fit is left out or cut short by
/// the budgeter's rules, the user is told in the notices, and the request asks the model for no more than the tokens
/// reserved for the answer. Without limits the conversation is laid out as it is, with no cap on the answer. What was
/// cut is reported in <see cref="BuiltPrompt.ContextWarnings"/>, apart from the notices about how the prompt was laid out.
/// </para>
/// </remarks>
/// <param name="contexts">
/// Fits the conversation into the model's context window, and remembers what did not fit
/// (<see cref="IContextService.Prepare"/>).
/// </param>
/// <param name="clock">The clock the dated guidance reads; the system's when not given.</param>
/// <param name="memory">
/// What the user asked the Assistant to remember (Settings, under Memory): its notes follow the instructions, last, so that the rest of the system
/// message is the same with and without them. Without it nothing is added.
/// </param>
public sealed class PromptBuilder(IContextService contexts, TimeProvider? clock = null, IMemoryStore? memory = null)
{
    /// <summary>Creates a builder that estimates tokens with the <see cref="HeuristicTokenEstimator"/>.</summary>
    public PromptBuilder()
        : this(new ContextBudgeter(new HeuristicTokenEstimator()))
    {
    }

    /// <summary>Creates a builder with a context service of its own around <paramref name="budgeter"/>.</summary>
    public PromptBuilder(ContextBudgeter budgeter)
        : this(new ContextService(budgeter))
    {
    }

    /// <summary>Tells the user that screenshots were sent as their recognized text.</summary>
    public const string ScreenshotTextNotice =
        "The local model can't read images, so screenshots were replaced by their text.";

    /// <summary>Tells the user that screenshots without recognized text were not sent.</summary>
    public const string ScreenshotDroppedNotice =
        "The local model can't read images, so screenshots without text were left out.";

    /// <summary>Tells the user that attached images were sent as their recognized text.</summary>
    public const string ImageTextNotice =
        "The local model can't read images, so attached images were replaced by their text.";

    /// <summary>Tells the user that attached images without recognized text were not sent.</summary>
    public const string ImageDroppedNotice =
        "The local model can't read images, so attached images without text were left out.";

    /// <summary>Tells the user that context with no content was not sent.</summary>
    public const string EmptyContextNotice =
        "Some of the attached items had no content the model could read, so they were left out.";

    /// <summary>
    /// Builds the request that asks the model to answer the user's last message of <paramref name="conversation"/>, which is the
    /// conversation's last message or, when the model has called a tool while answering it, the one before the tool's result.
    /// </summary>
    /// <param name="instructions">System instructions, or <see langword="null"/> for the default ones.</param>
    /// <param name="conversation">
    /// The conversation, oldest first, ending with the user's message to answer, or with the result of a tool the model called while
    /// answering it. Its messages are not changed.
    /// </param>
    /// <param name="model">
    /// The model the request is for, which decides how screenshots and images are sent and how big its context window
    /// is.
    /// </param>
    /// <param name="limits">
    /// The user's context limits, to fit the conversation into the model's context window under, or
    /// <see langword="null"/> to send it as it is.
    /// </param>
    /// <param name="conversationId">
    /// The conversation the messages belong to, which the context service remembers what it left out for; without one
    /// nothing is remembered.
    /// </param>
    /// <param name="tools">
    /// The tools the model may call in this request; the instructions then tell it how to use them, and what they cost is counted in
    /// the prompt. None by default.
    /// </param>
    /// <param name="finalAnswer">
    /// Whether this is the last request of an agent run (step 114), in which the model must answer in words: the instructions then end with
    /// <see cref="AssistantInstructions.FinalAnswerGuidance"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="conversation"/> does not end with a user message or a tool's result.</exception>
    public BuiltPrompt Build(
        string? instructions,
        IReadOnlyList<Message> conversation,
        ModelInfo model,
        ContextLimitSettings? limits = null,
        Guid? conversationId = null,
        IReadOnlyList<ToolDefinition>? tools = null,
        bool finalAnswer = false)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(model);
        var answered = ContextBudgeter.AnsweredIndex(conversation);
        if (answered < 0)
        {
            throw new ArgumentException("The conversation must end with the user's message.", nameof(conversation));
        }

        tools ??= [];
        var system = BuildInstructions(instructions, conversation, tools, model, (clock ?? TimeProvider.System).GetLocalNow(), (clock ?? TimeProvider.System).LocalTimeZone);
        if (memory?.ForPrompt() is { } remembered)
        {
            system += "\n\n" + remembered;
        }

        if (finalAnswer)
        {
            system += "\n\n" + AssistantInstructions.FinalAnswerGuidance;
        }

        // What the tools' descriptions cost goes with the instructions, which are never cut.
        var measured = tools.Count == 0
            ? system
            : system + "\n" + string.Join('\n', tools.Select(tool => tool.Name + tool.Description + tool.InputSchemaJson));
        var fit = limits is null ? null : contexts.Prepare(conversationId ?? Guid.Empty, measured, conversation, model, limits);
        if (fit is not null)
        {
            conversation = fit.Messages;
        }

        var notes = new Notes();
        var messages = new List<Message>(conversation.Count);
        for (var index = 0; index < conversation.Count; index++)
        {
            var message = conversation[index];
            switch (message.Role)
            {
                case MessageRole.User:
                    AddUserMessage(messages, message, index == answered, model, notes);
                    break;
                case MessageRole.Assistant when message.Text.Length == 0 && message.ToolCalls.Count == 0:
                    // Nothing was said (an answer stopped before its first words): there is nothing to tell the model.
                    break;
                default:
                    messages.Add(message with { ContextItems = [] });
                    break;
            }
        }

        var request = new ModelRequest(system, messages)
        {
            Tools = tools,
            Images = notes.Images,
            MaxOutputTokens = fit?.MaxOutputTokens,
        };
        return new BuiltPrompt(request, notes.ToNotices())
        {
            Budget = fit?.Report,
            ContextWarnings = fit?.Notices ?? [],
            ImageContents = notes.Contents,
        };
    }

    /// <summary>
    /// Whether a screenshot or an attached image goes to the model as an image: it can read images, and the item has
    /// pixels. The builder and the budgeter both ask, so they agree on what a prompt carries.
    /// </summary>
    internal static bool SendsAsImage(ContextItem item, ModelInfo model) =>
        IsPicture(item.Type) && model.SupportsVision && !item.ImageData.IsEmpty;

    // The kinds of context whose content is pixels, with recognized text as the stand-in for a model that cannot see.
    private static bool IsPicture(ContextItemType type) =>
        type is ContextItemType.Screenshot or ContextItemType.Image;

    // The caller's instructions, the guidance on untrusted context, and, while any message of the conversation carries the text of
    // a file, the guidance on what that text is. It is decided on the whole conversation, before anything is cut, so every turn
    // of a conversation that has a file in it starts with the same system message and the engine's prompt cache stays valid.
    private static string BuildInstructions(
        string? instructions, IReadOnlyList<Message> conversation, IReadOnlyList<ToolDefinition> tools, ModelInfo model, DateTimeOffset now, TimeZoneInfo zone)
    {
        var hasTools = tools.Count > 0;
        var system = (string.IsNullOrWhiteSpace(instructions) ? AssistantInstructions.Default : instructions.Trim())
            + "\n\n" + (hasTools ? AssistantInstructions.ToolGuidance : AssistantInstructions.NoToolGuidance)
            + "\n\n" + AssistantInstructions.UntrustedContextGuidance;

        if (tools.Any(tool => tool.Name == "search_web"))
            system += "\n\n" + AssistantInstructions.WebSearchGuidance(now);

        // Tools of the user's connected apps are said to be that, and what they return data (step 104); a conversation that is not offered
        // any is told nothing, so its prompt is the same as before.
        if (tools.Any(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)))
        {
            system += "\n\n" + AssistantInstructions.ConnectedAppToolsGuidance + " " + AssistantInstructions.ConnectedAppDates(now, zone);
        }

        // The calendar tools (step 111) come with how their dates are written and what today is, so that a small model that does not know the date can work out
        // "tomorrow"; a request that is not offered them is told nothing, so its prompt is the same as before.
        var readsCalendar = tools.Any(tool => CalendarToolResults.IsCalendarTool(tool.Name));
        if (readsCalendar)
        {
            system += "\n\n" + AssistantInstructions.CalendarToolsGuidance(now);
        }

        // A calendar service that is a connected app (step 116) is read by its own tools, which need the same help with dates; the built-in calendar tools stand aside for it.
        else if (tools.Any(CalendarToolResults.IsConnectedCalendarReader))
        {
            readsCalendar = true;
            system += "\n\n" + AssistantInstructions.ConnectedCalendarGuidance(now);
        }

        // The messaging tools (step 113) come with the rules for using them: a draft first, and a send only when the user says so.
        var messages = tools.Any(tool => MessagingToolResults.IsMessagingTool(tool.Name));
        if (messages)
        {
            system += "\n\n" + AssistantInstructions.MessagingToolsGuidance;
        }

        // A request that joins the two (check the calendar, then tell someone) is told the order to do it in (step 116).
        if (readsCalendar && messages)
        {
            system += "\n\n" + AssistantInstructions.CalendarMessageWorkflowGuidance;
        }

        if (Carries(conversation, ContextItemType.File))
        {
            system += "\n\n" + AssistantInstructions.FileContextGuidance;
        }

        if (conversation.Any(message => message.ContextItems.Any(item => item.WebPage is { IsEmpty: false } && !string.IsNullOrWhiteSpace(item.Text))))
        {
            system += "\n\n" + AssistantInstructions.WebSelectionGuidance;
        }

        // The page's text around a selection (step 88) is said to be only that, so the model does not take it for the page.
        if (conversation.Any(message => message.ContextItems.Any(
                item => item.Type == ContextItemType.Page && item.WebPage is { IsEmpty: false } && !string.IsNullOrWhiteSpace(item.Text))))
        {
            system += "\n\n" + AssistantInstructions.WebNearbyContextGuidance;
        }

        // A conversation about a screenshot says what one is, from its first question to its last: the screenshot's pixels are only with
        // the question they were sent with (or with every question, while it is retained), but "this" stays about it, and the descriptor
        // stays in every message that carried it.
        if (conversation.Any(message => message.ContextItems.Any(item => item.Type == ContextItemType.Screenshot)))
        {
            system += "\n\n" + (model.SupportsVision ? AssistantInstructions.ScreenshotGuidance : AssistantInstructions.ScreenshotTextGuidance);

            // The tool that reads its words is offered only in such a conversation, and the model is told when to use it.
            if (tools.Any(tool => tool.Name == ScreenToolResults.ReadScreenText))
            {
                system += "\n\n" + AssistantInstructions.ScreenTextToolGuidance;
            }
        }

        // Notes taken on files that were too long to read at once are said to be notes, so the answer is put together from them.
        return Carries(conversation, ContextItemType.FileNotes) ? system + "\n\n" + AssistantInstructions.FileNotesGuidance : system;
    }

    // Whether any message of the conversation carries context of the kind with text in it.
    private static bool Carries(IReadOnlyList<Message> conversation, ContextItemType type) =>
        conversation.Any(message => message.ContextItems.Any(item => item.Type == type && !string.IsNullOrWhiteSpace(item.Text)));

    // A question the model never answered (stopped or failed before its first words) leaves two user messages in a row,
    // which some chat templates refuse, so they are folded into one.
    private static void AddUserMessage(List<Message> messages, Message message, bool newest, ModelInfo model, Notes notes)
    {
        var text = RenderUserMessage(message, newest, model, notes);
        if (messages.Count > 0 && messages[^1].Role == MessageRole.User)
        {
            var previous = messages[^1];
            messages[^1] = previous with { Text = JoinParagraphs(previous.Text, text) };
        }
        else
        {
            messages.Add(message with { Text = text, ContextItems = [] });
        }
    }

    // The message's context first, each piece wrapped as untrusted, then what the user typed.
    private static string RenderUserMessage(Message message, bool newest, ModelInfo model, Notes notes)
    {
        var parts = new List<string>();
        var id = 0;
        foreach (var item in message.ContextItems)
        {
            var unreadPicture = IsPicture(item.Type) && !model.SupportsVision;
            if (SendsAsImage(item, model))
            {
                // Pixels travel beside the newest message; earlier ones are not sent again.
                if (newest)
                {
                    notes.Images.Add(item.ImageData);
                    notes.Contents.Add(item.Type == ContextItemType.Screenshot ? ImageContent.Screenshot : ImageContent.Picture);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                // The user was told when they asked; only the newest message's omissions are news.
                if (newest)
                {
                    if (unreadPicture)
                    {
                        notes.PictureDropped(item.Type);
                    }
                    else
                    {
                        notes.EmptyContext = true;
                    }
                }

                continue;
            }

            if (newest && unreadPicture)
            {
                notes.PictureAsText(item.Type);
            }

            parts.Add(UntrustedContext.Wrap(++id, KindOf(item.Type), item.DisplayName, item.Text, WebAttributes(item)));
        }

        if (message.Text.Length > 0)
        {
            parts.Add(message.Text);
        }

        return string.Join("\n\n", parts);
    }

    // Where a selection from a web page came from, for the model to know what the text is part of: the page's title and address are as
    // untrusted as the text, so they sit in the block's tag, cleaned like its name.
    private static IReadOnlyList<KeyValuePair<string, string?>>? WebAttributes(ContextItem item) =>
        item.WebPage is { IsEmpty: false } web
            ?
            [
                new("browser", web.BrowserName),
                new("page_title", web.Title),
                new("page_url", web.Url),
            ]
            : null;

    private static string JoinParagraphs(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : first + "\n\n" + second;

    private static string KindOf(ContextItemType type) => type switch
    {
        ContextItemType.Selection => "selection",
        ContextItemType.File => "file",
        ContextItemType.Page => "page",
        ContextItemType.Screenshot => "screenshot_text",
        ContextItemType.SearchResults => "search_results",
        ContextItemType.Image => "image_text",
        ContextItemType.FileNotes => "file_notes",
        _ => "context",
    };

    // What building the prompt gathers besides its messages.
    private sealed class Notes
    {
        public List<ReadOnlyMemory<byte>> Images { get; } = [];

        // What each of the images shows, in the same order: a part of the screen is made ready differently from a photo.
        public List<ImageContent> Contents { get; } = [];

        public bool ScreenshotAsText { get; set; }

        public bool ScreenshotDropped { get; set; }

        public bool ImageAsText { get; set; }

        public bool ImageDropped { get; set; }

        public bool EmptyContext { get; set; }

        public void PictureAsText(ContextItemType type)
        {
            if (type == ContextItemType.Screenshot)
            {
                ScreenshotAsText = true;
            }
            else
            {
                ImageAsText = true;
            }
        }

        public void PictureDropped(ContextItemType type)
        {
            if (type == ContextItemType.Screenshot)
            {
                ScreenshotDropped = true;
            }
            else
            {
                ImageDropped = true;
            }
        }

        public IReadOnlyList<string> ToNotices()
        {
            var notices = new List<string>();
            if (ScreenshotAsText)
            {
                notices.Add(ScreenshotTextNotice);
            }

            if (ScreenshotDropped)
            {
                notices.Add(ScreenshotDroppedNotice);
            }

            if (ImageAsText)
            {
                notices.Add(ImageTextNotice);
            }

            if (ImageDropped)
            {
                notices.Add(ImageDroppedNotice);
            }

            if (EmptyContext)
            {
                notices.Add(EmptyContextNotice);
            }

            return notices;
        }
    }
}
