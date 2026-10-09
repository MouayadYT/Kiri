using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Capture;

/// <summary>
/// The parts of the screen that conversations are about (PROJECT_SPEC §4.6): where a capture the user asked about is given to the
/// context service, kept for the conversation so that follow-ups ("the second row", "that error") need no new capture, and, when the
/// user takes it off, let go of everywhere it is held. The picture lives in memory only: in the capture itself, in the context service
/// and in the messages the model was asked with, and what OCR read from it is kept beside it; removing it clears all four.
/// </summary>
/// <remarks>
/// <para>
/// The context item is <see cref="ContextItemType.Screenshot"/> (a captured screen region) that the user picked on purpose, so it
/// ranks with the pictures and files they attach, and it is <see cref="ContextItem.Retained"/>: it goes with every question of the
/// conversation until it is released. Nothing about it is logged: not its pixels, and not what is in it.
/// </para>
/// <para>
/// A capture does not stay for ever: one that no question has been asked about for <see cref="IdleLifetime"/> is let go of by
/// <see cref="ReleaseIdle"/>, which the application calls every minute, and every capture is gone with the application. Until then
/// it is in memory only: nothing here writes it anywhere (PROJECT_SPEC §3.5).
/// </para>
/// </remarks>
public sealed class ScreenAttachments(
    IContextService contexts, IAnswerProvider? answers = null, IScreenText? screenText = null, TimeProvider? time = null)
{
    /// <summary>Who supplies the context that goes through here, as its provenance says.</summary>
    public const string Origin = "visual-intelligence";

    /// <summary>How long a capture is kept after it was attached or last asked about, before it is let go of.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    private readonly HashSet<Guid> _handedOver = [];
    private readonly Dictionary<Guid, Held> _held = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Raised when a capture was let go of, so that every view of the conversation takes its chip off.</summary>
    public event EventHandler<ImageItem>? Released;

    /// <summary>
    /// Raised when a part of the screen was attached to a conversation that is already open (the model took a screenshot in it,
    /// <see cref="AttachToConversation"/>), so that every view of that conversation shows its chip, as for one the user captured.
    /// </summary>
    public event EventHandler<ScreenAttachment>? Attached;

    /// <summary>
    /// Gives <paramref name="capture"/> to the context service as a screenshot of the conversation <paramref name="conversationId"/>,
    /// waiting for its next question and every one after it.
    /// </summary>
    public void Attach(Guid conversationId, ImageItem capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCapture || capture.Data.IsEmpty)
        {
            return;
        }

        contexts.Add(
            conversationId,
            new ContextItem(capture.ContextId, ContextItemType.Screenshot, capture.Name)
            {
                ImageData = capture.Data,
                Source = ContextSource.UserSelected,
                Retained = true,
            },
            Origin);
        _held[capture.ContextId] = new Held(conversationId, capture, _time.GetUtcNow());
    }

    /// <summary>
    /// Attaches <paramref name="capture"/> to the conversation <paramref name="conversationId"/>, which is already open and whose views
    /// are told (<see cref="Attached"/>) so that each shows its chip: what happens when the model takes a screenshot in the middle of an
    /// answer. Otherwise as <see cref="Attach"/>. Call it on the UI thread.
    /// </summary>
    public void AttachToConversation(Guid conversationId, ImageItem capture)
    {
        Attach(conversationId, capture);
        if (capture.IsCapture && !capture.Data.IsEmpty)
        {
            Attached?.Invoke(this, new ScreenAttachment(conversationId, capture));
        }
    }

    /// <summary>
    /// Says that the conversation <paramref name="conversationId"/> was asked something, so that the captures it carries are kept for
    /// <see cref="IdleLifetime"/> from now.
    /// </summary>
    public void Touch(Guid conversationId)
    {
        var now = _time.GetUtcNow();
        foreach (var held in _held.Values.Where(held => held.ConversationId == conversationId))
        {
            held.LastUsed = now;
        }
    }

    /// <summary>Lets go of every capture that has been idle for <see cref="IdleLifetime"/>. Call it on the UI thread.</summary>
    /// <returns>How many were let go of.</returns>
    public int ReleaseIdle()
    {
        var now = _time.GetUtcNow();
        var idle = _held.Values.Where(held => now - held.LastUsed >= IdleLifetime).ToList();
        foreach (var held in idle)
        {
            Release(held.ConversationId, held.Capture);
        }

        return idle.Count;
    }

    /// <summary>
    /// Says that the History window has taken over the conversation <paramref name="conversationId"/>, which goes on there with its
    /// screenshots: the floating conversation that is replaced by another no longer lets them go (<see cref="Abandon"/>).
    /// </summary>
    public void HandOver(Guid conversationId)
    {
        if (_handedOver.Count >= 64)
        {
            _handedOver.Clear();
        }

        _handedOver.Add(conversationId);
    }

    /// <summary>
    /// The conversation <paramref name="conversationId"/> is left behind by the view that held it: its capture is let go of, unless
    /// the conversation was handed over to the History window.
    /// </summary>
    public void Abandon(Guid conversationId, ImageItem capture)
    {
        if (!_handedOver.Contains(conversationId))
        {
            Release(conversationId, capture);
        }
    }

    /// <summary>
    /// Lets go of <paramref name="capture"/>: it is taken back from the context service, the conversation's memory of what the model was
    /// asked drops its pixels, and the capture itself is emptied. The messages keep that a screenshot was asked about.
    /// </summary>
    public void Release(Guid conversationId, ImageItem capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCapture)
        {
            return;
        }

        _held.Remove(capture.ContextId);
        contexts.Remove(conversationId, capture.ContextId);
        answers?.ReleaseContext(conversationId, capture.ContextId);
        screenText?.Forget(capture.ContextId);
        capture.Release();
        Released?.Invoke(this, capture);
    }

    private sealed class Held(Guid conversationId, ImageItem capture, DateTimeOffset lastUsed)
    {
        public Guid ConversationId { get; } = conversationId;

        public ImageItem Capture { get; } = capture;

        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}

/// <summary>A part of the screen that was attached to a conversation that is already open.</summary>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="Capture">The capture, in memory.</param>
public sealed record ScreenAttachment(Guid ConversationId, ImageItem Capture);
