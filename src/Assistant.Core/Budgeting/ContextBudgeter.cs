using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;

namespace Assistant.Core.Budgeting;

/// <summary>
/// Fits a conversation into the model's context window (PROJECT_SPEC §5.5): estimates what each part of the prompt
/// costs, holds the request to the normal or the heavy limit, and cuts what does not fit by fixed rules, the same way
/// every time.
/// </summary>
/// <remarks>
/// <para>
/// The window's share for the prompt is the limit for the request's <see cref="ContextBudgetMode"/> (or the model's
/// own window, when that is smaller) less the tokens reserved for the answer. It is spent in this order of priority,
/// and whatever is left when a step ends goes to the next:
/// </para>
/// <list type="number">
/// <item><description>
/// The system instructions and the fixed cost of the request. They are never cut.
/// </description></item>
/// <item><description>
/// The user's message, which is being answered. It is cut, its middle first, only if it alone does not fit.
/// </description></item>
/// <item><description>
/// The earlier turns of the conversation, newest first, each kept whole or not at all, so what is kept is an unbroken
/// run that starts with a user message. The first turn that does not fit ends the run, and older ones go with it.
/// </description></item>
/// <item><description>
/// Context, served by the rules of <see cref="ContextPriorityRules"/>, each rank after the one before it: what the user
/// explicitly selected for this question (files, pictures), then what was captured from the screen for it (the selected
/// text, a screenshot, a page), then what a search or a tool returned for it, then what was attached to earlier messages
/// (newest message first), and last what was retrieved for earlier messages. Items of one rank share what is left
/// fairly: an item that needs less than an equal share is sent whole, and the rest split what remains equally, each cut
/// to the start of its text. An item that would get too little to be useful, and an image that does not fit, is left
/// out, the last in order first.
/// </description></item>
/// </list>
/// <para>
/// A conversation that carries any context is held to the heavy limit, and any other to the normal one. Estimates come
/// from an <see cref="ITokenEstimator"/> and lean high, and every piece adds a fixed allowance for the way the prompt
/// wraps it, so an estimate that fits is unlikely to be refused by the engine. The conversation given is never
/// changed: the result is a copy, and the conversation keeps everything for later turns.
/// </para>
/// </remarks>
public sealed class ContextBudgeter(ITokenEstimator estimator)
{
    /// <summary>What every request costs beyond its messages: the start of the prompt and the chat template's frame.</summary>
    public const int RequestOverheadTokens = 16;

    /// <summary>What each message costs beyond its text: the chat template's markers for its role.</summary>
    public const int MessageOverheadTokens = 8;

    /// <summary>
    /// What each piece of context costs beyond its label and text: the tags that wrap it as untrusted, with room for
    /// the longest kind and a two-digit id, and the blank line before the next piece.
    /// </summary>
    public const int ContextBlockOverheadTokens = 40;

    /// <summary>What an image costs, whatever its size: models differ, and this leans high.</summary>
    public const int ImageTokens = 1024;

    /// <summary>The least text of a piece of context worth sending when it has to be cut short.</summary>
    public const int MinShortenedTokens = 32;

    /// <summary>
    /// The least the user's own message is cut down to, even when the prompt is then over budget: one no longer than
    /// this is never cut.
    /// </summary>
    public const int MinQuestionTokens = 64;

    /// <summary>Ends a piece of context that was cut short, so the model knows the text goes on.</summary>
    public const string ContextCutMarker = "\n\n[... the rest was left out because it did not fit ...]";

    /// <summary>Stands where the middle of the user's own message was cut out.</summary>
    public const string MessageCutMarker = "\n[... the middle was left out because it did not fit ...]\n";

    private readonly int _contextCutMarkerTokens = estimator.Estimate(ContextCutMarker);
    private readonly int _messageCutMarkerTokens = estimator.Estimate(MessageCutMarker);

    /// <summary>
    /// The limit a conversation is held to: heavy when any message carries context with something in it, or the result of a tool
    /// (which may be the text of a file), normal otherwise.
    /// </summary>
    public static ContextBudgetMode ModeOf(IReadOnlyList<Message> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Any(message => message.Role == MessageRole.Tool || message.ContextItems.Any(HasContent))
            ? ContextBudgetMode.Heavy
            : ContextBudgetMode.Normal;
    }

    /// <summary>
    /// The index of the user's message that a conversation asks the model to answer: its last message when that is the user's, and
    /// when a turn is going on (the model has called a tool and been given its result) the user's message that turn began with. -1 when
    /// the conversation is not one to answer: it is empty or its last message is neither the user's nor a tool's result.
    /// </summary>
    public static int AnsweredIndex(IReadOnlyList<Message> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Count == 0 || conversation[^1].Role is not (MessageRole.User or MessageRole.Tool))
        {
            return -1;
        }

        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            if (conversation[index].Role == MessageRole.User)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Fits <paramref name="conversation"/>, and the answer to it, into <paramref name="model"/>'s context window.</summary>
    /// <param name="instructions">
    /// The system instructions exactly as they are sent (with the guidance on untrusted context): never cut.
    /// </param>
    /// <param name="conversation">
    /// The conversation, oldest first, ending with the user's message to answer, or with the result of a tool the model called while
    /// answering it (the turn's own messages after the user's are kept whole, and are costed before anything else is).
    /// </param>
    /// <param name="model">The model, whose context length is the window.</param>
    /// <param name="limits">The user's limits.</param>
    /// <exception cref="ArgumentException"><paramref name="conversation"/> does not end with a user message.</exception>
    public BudgetedConversation Fit(
        string instructions,
        IReadOnlyList<Message> conversation,
        ModelInfo model,
        ContextLimitSettings limits)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(limits);
        var newest = AnsweredIndex(conversation);
        if (newest < 0)
        {
            throw new ArgumentException("The conversation must end with the user's message.", nameof(conversation));
        }

        var mode = ModeOf(conversation);
        var budget = ContextBudget.Resolve(model, limits, mode);
        var remaining = budget.PromptTokens - RequestOverheadTokens - estimator.Estimate(instructions);

        // What the turn has gone through since the user asked (a tool called, its result) is sent whole.
        for (var index = newest + 1; index < conversation.Count; index++)
        {
            remaining -= CostOf(conversation[index]);
        }

        var (question, questionShortened) = FitQuestion(conversation[newest].Text, ref remaining);
        var (firstKept, turnsKept, turnsLeftOut) = FitTurns(conversation, newest, ref remaining);
        var slots = CollectContext(conversation, firstKept, newest, model);
        FitContext(slots, ref remaining);
        var items = FitsOf(conversation, firstKept, model, slots);

        var report = new ContextBudgetReport(
            mode,
            budget,
            budget.PromptTokens - remaining,
            turnsKept,
            turnsLeftOut,
            slots.Count(slot => slot.Fate == ContextFate.Whole),
            slots.Count(slot => slot.Fate == ContextFate.Shortened),
            slots.Count(slot => slot.Fate == ContextFate.LeftOut),
            questionShortened)
        {
            Items = items,
        };
        var messages = report.AnythingTrimmed
            ? Assemble(conversation, firstKept, newest, question, questionShortened, slots)
            : conversation;
        return new BudgetedConversation(messages, budget.ReservedOutputTokens, NoticesFor(report, slots), report);
    }

    // The user's message is kept whole unless it alone is more than is left, when it loses its middle: the ends are
    // where people put what they want. It is never cut below a useful size, even if the prompt is then over budget.
    private (string Text, bool Shortened) FitQuestion(string text, ref int remaining)
    {
        var tokens = estimator.Estimate(text);
        var cost = tokens + MessageOverheadTokens;
        if (cost <= remaining || tokens <= MinQuestionTokens)
        {
            remaining -= cost;
            return (text, false);
        }

        var allowance = Math.Max(remaining - MessageOverheadTokens, MinQuestionTokens);
        var room = allowance - _messageCutMarkerTokens;
        var shortened = TokenTrimmer.Head(estimator, text, (room + 1) / 2)
            + MessageCutMarker
            + TokenTrimmer.Tail(estimator, text, room / 2);
        remaining -= estimator.Estimate(shortened) + MessageOverheadTokens;
        return (shortened, true);
    }

    // Walks back over the earlier turns, newest first, keeping each whole while it fits. Returns where the kept run
    // starts, and how many turns were kept and left out.
    private (int FirstKept, int Kept, int LeftOut) FitTurns(IReadOnlyList<Message> conversation, int newest, ref int remaining)
    {
        var firstKept = newest;
        var kept = 0;
        var turnEnd = firstKept;
        while (turnEnd > 0)
        {
            var turnStart = StartOfTurn(conversation, turnEnd);
            var cost = 0;
            for (var index = turnStart; index < turnEnd; index++)
            {
                cost += CostOf(conversation[index]);
            }

            if (cost > remaining)
            {
                break;
            }

            remaining -= cost;
            firstKept = turnStart;
            kept++;
            turnEnd = turnStart;
        }

        var leftOut = 0;
        while (turnEnd > 0)
        {
            turnEnd = StartOfTurn(conversation, turnEnd);
            leftOut++;
        }

        return (firstKept, kept, leftOut);
    }

    // A turn is the user's messages (more than one when an earlier question was never answered) and the answers that
    // follow them; the one that ends at `end` starts after the last message of the turn before.
    private static int StartOfTurn(IReadOnlyList<Message> conversation, int end)
    {
        var start = end;
        while (start > 0 && conversation[start - 1].Role != MessageRole.User)
        {
            start--;
        }

        while (start > 0 && conversation[start - 1].Role == MessageRole.User)
        {
            start--;
        }

        return start;
    }

    // What a message costs as the prompt sends it: its text and tool calls, not its context, which is costed apart.
    // An answer that said nothing is not sent at all.
    private int CostOf(Message message)
    {
        if (message.Role == MessageRole.Assistant && message.Text.Length == 0 && message.ToolCalls.Count == 0)
        {
            return 0;
        }

        var cost = MessageOverheadTokens + estimator.Estimate(message.Text);
        foreach (var call in message.ToolCalls)
        {
            cost += estimator.Estimate(call.ToolName) + estimator.Estimate(call.ArgumentsJson) + 4;
        }

        return cost;
    }

    private static bool HasContent(ContextItem item) => !string.IsNullOrWhiteSpace(item.Text) || !item.ImageData.IsEmpty;

    // One piece of context that the prompt would carry, with what it costs and what becomes of it. `Priority` decides who
    // is served first (ContextPriorityRules).
    private sealed class Slot(int messageIndex, int itemIndex, ContextItem item, ContextPriority priority, bool isImage, int fixedTokens, int bodyTokens)
    {
        public int MessageIndex { get; } = messageIndex;

        public int ItemIndex { get; } = itemIndex;

        public ContextItem Item { get; } = item;

        public ContextPriority Priority { get; } = priority;

        public bool IsImage { get; } = isImage;

        public int FixedTokens { get; } = fixedTokens;

        public int BodyTokens { get; set; } = bodyTokens;

        public ContextFate Fate { get; set; } = ContextFate.Whole;

        public string? ShortenedText { get; set; }

        public int Cost => FixedTokens + BodyTokens;
    }

    // The context of the kept messages that the prompt would carry, in the order it is served: by priority, then earlier
    // messages newest first, then in the order attached. What the prompt builder skips has no slot: an image that goes
    // to the model once, with the message it came with, and an item with nothing in it.
    private List<Slot> CollectContext(IReadOnlyList<Message> conversation, int firstKept, int newest, ModelInfo model)
    {
        var slots = new List<Slot>();
        for (var messageIndex = firstKept; messageIndex < conversation.Count; messageIndex++)
        {
            var items = conversation[messageIndex].ContextItems;
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                var item = items[itemIndex];
                var priority = ContextPriorityRules.Of(item, messageIndex == newest);
                if (PromptBuilder.SendsAsImage(item, model))
                {
                    if (messageIndex == newest)
                    {
                        slots.Add(new Slot(messageIndex, itemIndex, item, priority, true, ImageTokens, 0));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    // The prompt cuts a label to 100 characters.
                    var label = item.DisplayName.Length > 100 ? item.DisplayName[..100] : item.DisplayName;
                    slots.Add(new Slot(
                        messageIndex,
                        itemIndex,
                        item,
                        priority,
                        false,
                        ContextBlockOverheadTokens + estimator.Estimate(label),
                        estimator.Estimate(item.Text)));
                }
            }
        }

        return [.. slots.OrderBy(slot => slot.Priority).ThenByDescending(slot => slot.MessageIndex).ThenBy(slot => slot.ItemIndex)];
    }

    // What became of each piece of context the prompt would carry, in the order it was served. The context of turns that
    // were left out is left out with them, after the rest.
    private static List<ContextItemFit> FitsOf(IReadOnlyList<Message> conversation, int firstKept, ModelInfo model, List<Slot> slots)
    {
        var fits = slots.ConvertAll(slot => new ContextItemFit(slot.Item.Id, slot.Priority, slot.Fate));
        for (var messageIndex = firstKept - 1; messageIndex >= 0; messageIndex--)
        {
            foreach (var item in conversation[messageIndex].ContextItems)
            {
                if (!PromptBuilder.SendsAsImage(item, model) && !string.IsNullOrWhiteSpace(item.Text))
                {
                    fits.Add(new ContextItemFit(item.Id, ContextPriorityRules.Of(item, false), ContextFate.LeftOut));
                }
            }
        }

        return fits;
    }

    // Serves the ranks in turn with what is left.
    private void FitContext(List<Slot> slots, ref int remaining)
    {
        foreach (var rank in slots.GroupBy(slot => slot.Priority).OrderBy(rank => rank.Key))
        {
            FitRank([.. rank], ref remaining);
        }
    }

    private void FitRank(List<Slot> group, ref int remaining)
    {
        // An image cannot be cut short: it is sent whole, or left out.
        foreach (var image in group.Where(slot => slot.IsImage))
        {
            if (image.Cost <= remaining)
            {
                remaining -= image.Cost;
            }
            else
            {
                image.Fate = ContextFate.LeftOut;
            }
        }

        var texts = group.Where(slot => !slot.IsImage).ToList();

        // When there is not enough for all, too many would each get too little to be worth sending: the last go.
        while (texts.Count > 0
               && texts.Sum(slot => slot.Cost) > remaining
               && remaining / texts.Count < texts.Max(slot => slot.FixedTokens) + _contextCutMarkerTokens + MinShortenedTokens)
        {
            texts[^1].Fate = ContextFate.LeftOut;
            texts.RemoveAt(texts.Count - 1);
        }

        // Smallest first, each gets what it needs or an equal share of what is left. What a small one leaves over goes
        // to the larger, so no share is below the first equal share.
        var left = texts.Count;
        foreach (var slot in texts.OrderBy(slot => slot.Cost))
        {
            var share = remaining / left;
            left--;
            if (slot.Cost > share)
            {
                var text = TokenTrimmer.Head(estimator, slot.Item.Text!, share - slot.FixedTokens - _contextCutMarkerTokens);
                slot.ShortenedText = text + ContextCutMarker;
                slot.BodyTokens = estimator.Estimate(slot.ShortenedText);
                slot.Fate = ContextFate.Shortened;
            }

            remaining -= slot.Cost;
        }
    }

    // The conversation as the prompt is built from it: only the kept messages, the user's message as cut, and the
    // context as it was fitted. What the fitting did not touch is the very same object.
    private static List<Message> Assemble(
        IReadOnlyList<Message> conversation,
        int firstKept,
        int newest,
        string question,
        bool questionShortened,
        List<Slot> slots)
    {
        var messages = new List<Message>(conversation.Count - firstKept);
        for (var messageIndex = firstKept; messageIndex < conversation.Count; messageIndex++)
        {
            var message = conversation[messageIndex];
            var changed = slots.Where(slot => slot.MessageIndex == messageIndex && slot.Fate != ContextFate.Whole).ToList();
            if (changed.Count > 0)
            {
                var items = new List<ContextItem>(message.ContextItems.Count);
                for (var itemIndex = 0; itemIndex < message.ContextItems.Count; itemIndex++)
                {
                    var item = message.ContextItems[itemIndex];
                    var slot = changed.Find(candidate => candidate.ItemIndex == itemIndex);
                    if (slot is null)
                    {
                        items.Add(item);
                    }
                    else if (slot.Fate == ContextFate.Shortened)
                    {
                        items.Add(item with { Text = slot.ShortenedText });
                    }
                }

                message = message with { ContextItems = items };
            }

            if (questionShortened && messageIndex == newest)
            {
                message = message with { Text = question };
            }

            messages.Add(message);
        }

        return messages;
    }

    private static List<string> NoticesFor(ContextBudgetReport report, List<Slot> slots)
    {
        var notices = new List<string>();
        if (report.TurnsLeftOut > 0)
        {
            notices.Add(ContextBudgetNotices.EarlierMessagesLeftOut);
        }

        if (report.QuestionShortened)
        {
            notices.Add(ContextBudgetNotices.MessageShortened);
        }

        var shortened = slots.Where(slot => slot.Fate == ContextFate.Shortened).Select(slot => slot.Item.DisplayName).ToList();
        if (shortened.Count > 0)
        {
            notices.Add(ContextBudgetNotices.ContextShortened(shortened));
        }

        var leftOut = slots.Where(slot => slot.Fate == ContextFate.LeftOut).Select(slot => slot.Item.DisplayName).ToList();
        if (leftOut.Count > 0)
        {
            notices.Add(ContextBudgetNotices.ContextLeftOut(leftOut));
        }

        return notices;
    }
}
