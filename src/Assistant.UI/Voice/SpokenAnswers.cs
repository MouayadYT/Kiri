using System.Collections.Specialized;
using System.ComponentModel;
using Assistant.Core.Voice;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Voice;

/// <summary>
/// Says the Assistant's answers aloud (PROJECT_SPEC §4.2, step 125), on top of the text and never instead of it: the answer is drawn exactly as it is without
/// a voice, and the voice only listens to it. An answer to a request that was spoken is said as it is written; an answer to a typed request is only said
/// when the user asks for it with the speaker button.
/// </summary>
public interface ISpokenAnswers
{
    /// <summary>Whether an answer is being said, or waits for its first words to be said.</summary>
    bool IsSpeaking { get; }

    /// <summary>Raised, on the UI thread, when <see cref="IsSpeaking"/> changes.</summary>
    event EventHandler? SpeakingChanged;

    /// <summary>
    /// Says <paramref name="answer"/> as it is written, from its first words to its last: what it holds now and what is added to it until it ends. Any answer
    /// that is being said stops first. Call it on the UI thread.
    /// </summary>
    void Follow(MessageViewModel answer);

    /// <summary>Stops the voice at once: what is being said goes quiet, and what was queued is dropped. The answer itself keeps being written.</summary>
    void Stop();
}

/// <summary>The default <see cref="ISpokenAnswers"/>, over the text-to-speech service.</summary>
public sealed class SpokenAnswers : ISpokenAnswers, IDisposable
{
    private readonly ITextToSpeechService _speech;
    private readonly Action<Action>? _post;
    private Follower? _current;

    /// <summary>Creates the service.</summary>
    /// <param name="speech">The voice.</param>
    /// <param name="post">Runs an action on the UI thread, which is where <see cref="SpeakingChanged"/> is raised; without it, it is raised on the voice's own thread.</param>
    public SpokenAnswers(ITextToSpeechService speech, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(speech);
        _speech = speech;
        _post = post;
        _speech.SpeakingChanged += OnSpeakingChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? SpeakingChanged;

    /// <inheritdoc/>
    public bool IsSpeaking => _speech.IsSpeaking;

    /// <inheritdoc/>
    public void Follow(MessageViewModel answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _current?.Detach();
        _current = new Follower(_speech.BeginResponse(), answer);
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _current?.Detach();
        _current = null;
        _speech.StopAll();
    }

    /// <inheritdoc/>
    public void Dispose() => _speech.SpeakingChanged -= OnSpeakingChanged;

    private void OnSpeakingChanged(object? sender, EventArgs e)
    {
        if (_post is null)
        {
            SpeakingChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _post(() => SpeakingChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    // What is said of an answer, and when it is said: it listens to the answer's own parts as they grow and feeds the words to the voice.
    internal sealed class Follower
    {
        private readonly ISpokenResponse _response;
        private readonly MessageViewModel _answer;
        private readonly Dictionary<MessageContent, int> _said = [];
        private readonly List<TextContent> _watched = [];
        private bool _anythingSaid;
        private bool _detached;

        public Follower(ISpokenResponse response, MessageViewModel answer)
        {
            _response = response;
            _answer = answer;
            answer.Content.CollectionChanged += OnContentChanged;
            answer.PropertyChanged += OnAnswerChanged;
            _response.Finished += OnFinished;
            foreach (var content in answer.Content)
            {
                Watch(content);
            }

            Say();
            EndIfOver();
        }

        // The voice is told nothing more: the answer carries on without it.
        public void Detach()
        {
            if (_detached)
            {
                return;
            }

            _detached = true;
            _answer.Content.CollectionChanged -= OnContentChanged;
            _answer.PropertyChanged -= OnAnswerChanged;
            _response.Finished -= OnFinished;
            foreach (var text in _watched)
            {
                text.PropertyChanged -= OnTextChanged;
            }

            _watched.Clear();
        }

        private void OnContentChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (var content in e.NewItems.OfType<MessageContent>())
                {
                    Watch(content);
                }
            }

            Say();
        }

        private void OnTextChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TextContent.Text))
            {
                Say();
            }
        }

        private void OnAnswerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MessageViewModel.Status))
            {
                Say();
                EndIfOver();
            }
        }

        // Stopped by the user, or taken over by another answer: nothing more is said.
        private void OnFinished(object? sender, EventArgs e) => Detach();

        private void Watch(MessageContent content)
        {
            if (content is TextContent text)
            {
                text.PropertyChanged += OnTextChanged;
                _watched.Add(text);
            }
        }

        // The words the voice has not been given yet, in the order the answer has them.
        private void Say()
        {
            if (_detached)
            {
                return;
            }

            foreach (var content in _answer.Content)
            {
                switch (content)
                {
                    case TextContent text:
                        var heard = _said.GetValueOrDefault(content);
                        if (text.Text.Length > heard)
                        {
                            // Each part of the answer is a line of its own: a part that follows another does not run into it.
                            _response.Append((heard == 0 && _anythingSaid ? "\n" : "") + text.Text[heard..]);
                            _said[content] = text.Text.Length;
                            _anythingSaid = true;
                        }

                        break;

                    case RichAnswerCard card when !_said.ContainsKey(content):
                        _said[content] = 1;
                        _response.Append((_anythingSaid ? "\n" : "") + Spoken(card) + "\n");
                        _anythingSaid = true;
                        break;
                }
            }
        }

        // When the answer is over, the voice is told, so that it says the last of it and finishes. An answer the user stopped is not said any more.
        private void EndIfOver()
        {
            if (_detached || _answer.Status == MessageStatus.Answering)
            {
                return;
            }

            if (_answer.Status == MessageStatus.Stopped)
            {
                _response.Stop();
            }
            else
            {
                _response.Complete();
            }

            Detach();
        }

        // A result on its own, as it is read: "9 plus 10 equals 19".
        private static string Spoken(RichAnswerCard card)
        {
            var result = card.Result;
            if (card.Expression is not { } expression)
            {
                return $"{card.Label}: {result}.";
            }

            var words = expression.Replace("+", " plus ", StringComparison.Ordinal).Replace("*", " times ", StringComparison.Ordinal)
                .Replace("×", " times ", StringComparison.Ordinal).Replace("/", " divided by ", StringComparison.Ordinal)
                .Replace("÷", " divided by ", StringComparison.Ordinal).Replace("^", " to the power of ", StringComparison.Ordinal)
                .Replace(" - ", " minus ", StringComparison.Ordinal);
            return $"{words} equals {result}.";
        }
    }
}
