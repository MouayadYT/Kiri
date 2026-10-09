using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.Orb;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Voice;
using Assistant.UI.Windowing;
using Assistant.Windows.Audio;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

// Voice input, spoken answers, the speaker button and the wake word in the Assistant's window (PROJECT_SPEC §4.2, step 125), with fakes for the microphone, the
// recognizer and the voice: what is tested is that the pieces do what the user is promised.
public sealed partial class PromptInputControlTests
{
    // ---- spoken answers: the voice listens to an answer as it is written ----

    [Fact]
    public void AnAnswerIsSpokenAsItIsWrittenAndFinishesWhenItEnds() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech);
        var text = new TextContent("");
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        answer.Content.Add(text);

        spoken.Follow(answer);
        text.Text = "You have two meetings";
        text.Text = "You have two meetings tomorrow. The first";
        text.Text = "You have two meetings tomorrow. The first is the design review.";
        answer.Status = MessageStatus.Complete;

        var response = Assert.Single(speech.Responses);
        Assert.Equal("You have two meetings tomorrow. The first is the design review.", response.Said);
        Assert.True(response.Completed);
        Assert.False(response.WasStopped);
    });

    [Fact]
    public void ProseThatComesAfterACardIsSpokenAfterTheCardsPartsBeforeIt() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech);
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        var first = new TextContent("I will look that up.");
        answer.Content.Add(first);
        spoken.Follow(answer);

        answer.Content.Add(new CalculationResult("9 + 10", "19"));
        var second = new TextContent("");
        answer.Content.Add(second);
        second.Text = "That is nineteen.";
        answer.Status = MessageStatus.Complete;

        var said = System.Text.RegularExpressions.Regex.Replace(Assert.Single(speech.Responses).Said, "[ \t]+", " ");
        Assert.StartsWith("I will look that up.\n", said, StringComparison.Ordinal);
        Assert.Contains("9 plus 10 equals 19.\n", said, StringComparison.Ordinal);
        Assert.EndsWith("\nThat is nineteen.", said.TrimEnd(), StringComparison.Ordinal);
    });

    [Fact]
    public void AnAnswerThatWasStoppedIsNotSpokenAnyMore() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech);
        var text = new TextContent("Half of an answer");
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        answer.Content.Add(text);
        spoken.Follow(answer);

        answer.Status = MessageStatus.Stopped;
        text.Text += " that was never finished";

        var response = Assert.Single(speech.Responses);
        Assert.True(response.WasStopped);
        Assert.Equal("Half of an answer", response.Said);
    });

    [Fact]
    public void AnAnswerThatIsAlreadyWholeIsSpokenAtOnce() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var answer = new MessageViewModel(MessageRole.Assistant, "It is a quarter past three.");

        new SpokenAnswers(speech).Follow(answer);

        var response = Assert.Single(speech.Responses);
        Assert.Equal("It is a quarter past three.", response.Said);
        Assert.True(response.Completed);
    });

    [Fact]
    public void WhenTheVoiceIsStoppedTheAnswerKeepsGrowingButNothingMoreIsSaid() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech);
        var text = new TextContent("The first sentence is here. ");
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        answer.Content.Add(text);
        spoken.Follow(answer);

        speech.Responses[0].Stop();
        text.Text += "The second sentence must stay silent.";
        answer.Status = MessageStatus.Complete;

        Assert.Equal("The first sentence is here. ", speech.Responses[0].Said);
        Assert.Equal("The first sentence is here. The second sentence must stay silent.", text.Text);
        Assert.False(speech.Responses[0].Completed);
    });

    [Fact]
    public void FollowingAnotherAnswerLetsTheFirstGo() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech);
        var firstText = new TextContent("First.");
        var first = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Answering };
        first.Content.Add(firstText);
        spoken.Follow(first);
        var second = new MessageViewModel(MessageRole.Assistant, "Second.");

        spoken.Follow(second);
        firstText.Text += " More of the first.";

        Assert.Equal(2, speech.Responses.Count);
        Assert.Equal("First.", speech.Responses[0].Said);
        Assert.Equal("Second.", speech.Responses[1].Said);
    });

    [Fact]
    public void StoppingSilencesTheVoiceAtOnce() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech);
        spoken.Follow(new MessageViewModel(MessageRole.Assistant, "Something to say."));

        spoken.Stop();

        Assert.Equal(1, speech.StopAllCount);
    });

    [Fact]
    public void SpeakingChangesReachTheUiThread() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var spoken = new SpokenAnswers(speech, action => action());
        var changes = 0;
        spoken.SpeakingChanged += (_, _) => changes++;

        speech.SetSpeaking(true);
        speech.SetSpeaking(false);

        Assert.Equal(2, changes);
        Assert.False(spoken.IsSpeaking);
    });

    // ---- voice input: the microphone, the words and what is asked ----

    [Fact]
    public void VoiceInputShowsTheWordsAsTheyAreHeardAndAsksThemWhenTheSpeakerStops() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var heard = new List<VoiceUtterance>();
        voice.UtteranceEnded += (_, utterance) => heard.Add(utterance);

        voice.Start();
        Assert.True(voice.IsListening);
        Assert.True(voice.ShowsOrb);
        var listening = Assert.Single(input.Listenings);

        listening.Raise("what's on my", null);
        Pump();
        Assert.Equal("What's on my", voice.Transcript);
        Assert.True(voice.HasTranscript);
        listening.Raise("what's on my calendar tomorrow", null);
        Pump();
        Assert.Equal("What's on my calendar tomorrow", voice.Transcript);
        Assert.Empty(heard);

        listening.Raise("what's on my calendar tomorrow", SpeechEndReason.Endpoint);
        Pump();

        var utterance = Assert.Single(heard);
        Assert.Equal("What's on my calendar tomorrow?", utterance.Text);
        Assert.Equal(SpeechEndReason.Endpoint, utterance.Reason);
        Assert.False(utterance.FromWakeWord);
        Assert.False(voice.IsListening);
        Assert.False(voice.ShowsOrb);
        Assert.Equal("", voice.Transcript);
        Assert.True(listening.IsDisposed, "The microphone was left open after the request.");
    });

    [Fact]
    public void TurningVoiceInputOffThrowsAwayWhatWasHeard() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var heard = new List<VoiceUtterance>();
        voice.UtteranceEnded += (_, utterance) => heard.Add(utterance);
        voice.Start();
        var listening = input.Listenings[0];
        listening.Raise("something private", null);
        Pump();

        voice.Stop();
        listening.Raise("something private", SpeechEndReason.Finished);
        Pump();

        Assert.Empty(heard);
        Assert.False(voice.IsListening);
        Assert.Equal("", voice.Transcript);
        Assert.True(listening.IsDisposed);
    });

    [Fact]
    public void PressingTheMicrophoneAgainSaysTheSpeakerIsDoneAndAsksWhatWasHeard() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var heard = new List<VoiceUtterance>();
        voice.UtteranceEnded += (_, utterance) => heard.Add(utterance);
        voice.Toggle();
        var listening = input.Listenings[0];
        listening.Raise("open the settings", null);
        Pump();

        voice.Toggle();
        Assert.True(listening.FinishCalled);
        Assert.True(voice.IsListening, "The microphone stays on until the last words have been recognized.");
        listening.Raise("open the settings", SpeechEndReason.Finished);
        Pump();

        Assert.Equal("Open the settings", Assert.Single(heard).Text);
        Assert.False(voice.IsListening);
    });

    [Fact]
    public void SilenceEndsVoiceInputWithNothingToAsk() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var heard = new List<VoiceUtterance>();
        voice.UtteranceEnded += (_, utterance) => heard.Add(utterance);
        voice.Start();

        input.Listenings[0].Raise("", SpeechEndReason.NoSpeech);
        Pump();

        var utterance = Assert.Single(heard);
        Assert.False(utterance.HasRequest);
        Assert.Equal(SpeechEndReason.NoSpeech, utterance.Reason);
        Assert.False(voice.IsListening);
        Assert.Null(voice.FailureMessage);
    });

    [Fact]
    public void AMicrophoneFailureEndsVoiceInputWithAShortMessage() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        voice.Start();

        input.Listenings[0].Fail(MicrophoneFailure.AccessDenied);
        Pump();

        Assert.False(voice.IsListening);
        Assert.Equal("Microphone access is off in Settings", voice.FailureMessage);
    });

    [Fact]
    public void ARecognizerThatIsNotInstalledSaysSoInsteadOfDoingNothing() => RunSta(() =>
    {
        var input = new FakeVoiceInput { RecognizerStatus = new VoiceEngineStatus(VoiceEngineState.NotInstalled, "Not installed") };
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        voice.Start();

        input.Listenings[0].Raise("", SpeechEndReason.Finished);
        Pump();

        Assert.Equal("Speech recognition isn't installed", voice.FailureMessage);
    });

    [Fact]
    public void StartingVoiceInputInterruptsWhateverIsBeingSpokenAtOnce() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers { IsSpeaking = true };
        var voice = new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech);

        voice.Start();

        Assert.Equal(1, speech.StopCount);
    });

    [Fact]
    public void TheRequestAfterTheWakeWordStartsFromWhatWasHeardAroundIt() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        var heard = new List<VoiceUtterance>();
        voice.UtteranceEnded += (_, utterance) => heard.Add(utterance);
        var handoff = new WakeHandoff([1, 2, 3]);

        voice.Start(handoff);

        Assert.Same(handoff, input.Requests[0].Handoff);
        Assert.True(voice.StartedByWakeWord);
        input.Listenings[0].Raise("what time is it", SpeechEndReason.Endpoint);
        Pump();
        Assert.True(Assert.Single(heard).FromWakeWord);
        Assert.False(voice.StartedByWakeWord);
    });

    [Fact]
    public void WithoutSpeechRecognitionVoiceInputOnlyMeasuresTheMicrophoneAsBefore() => RunSta(() =>
    {
        var microphone = new FakeMicrophone();
        var voice = new VoiceInputViewModel(microphone);

        voice.Start();
        microphone.Current!.Level = 0.25;

        Assert.False(voice.RecognizesSpeech);
        Assert.True(voice.IsListening);
        Assert.False(voice.ShowsOrb);
        Assert.Equal(0.25, voice.ReadLevel());
        voice.Toggle();
        Assert.False(voice.IsListening);
    });

    [Fact]
    public void TheOrbAndTheGlowFollowTheSameMicrophoneLevel() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var voice = new VoiceInputViewModel(new FakeMicrophone(), input);
        voice.Start();
        var listening = input.Listenings[0];

        listening.Level = 0;
        Assert.Equal(0, voice.OrbAmplitude.ReadAmplitude());
        listening.Level = 0.05;
        var quiet = voice.OrbAmplitude.ReadAmplitude();
        Assert.Equal(0.05, voice.ReadLevel());
        listening.Level = 0.3;
        var loud = voice.OrbAmplitude.ReadAmplitude();

        Assert.Equal(0.3, voice.ReadLevel());
        Assert.True(quiet > 0);
        Assert.True(loud > quiet);
        Assert.InRange(loud, 0, 1);
    });

    // ---- the bar ----

    [Fact]
    public void ASpokenRequestInTheBarIsAskedAsSpokenWithoutSearchingForIt() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var bar = CreateBarModel(input: input);
        var asked = new List<(string Question, bool Spoken)>();
        bar.AskRequested += (_, question) => asked.Add((question, bar.AskingBySpeech));

        bar.Voice.Start();
        Assert.Equal(SearchOrAskViewModel.ListeningPlaceholder, bar.Placeholder);
        input.Listenings[0].Raise("what's on my calendar tomorrow", SpeechEndReason.Endpoint);
        Pump();

        Assert.Equal([("What's on my calendar tomorrow?", true)], asked);
        Assert.Equal("What's on my calendar tomorrow?", bar.Query);
        Assert.False(bar.AskingBySpeech);
        Assert.Equal("Search or Ask", bar.Placeholder);
    });

    [Fact]
    public void ATypedQuestionIsNotSpoken() => RunSta(() =>
    {
        var bar = CreateBarModel(input: new FakeVoiceInput());
        var spoken = new List<bool>();
        bar.AskRequested += (_, _) => spoken.Add(bar.AskingBySpeech);

        bar.Query = "what's on my calendar tomorrow";
        bar.AskCommand.Execute(null);

        Assert.Equal([false], spoken);
    });

    [Fact]
    public void NothingHeardAsksNothing() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var bar = CreateBarModel(input: input);
        var asked = 0;
        bar.AskRequested += (_, _) => asked++;
        bar.Voice.Start();

        input.Listenings[0].Raise("", SpeechEndReason.NoSpeech);
        Pump();

        Assert.Equal(0, asked);
        Assert.Equal("", bar.Query);
    });

    // ---- the conversation ----

    [Fact]
    public void ASpokenRequestIsAnsweredInTextAndAloud() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var answers = new StreamingAnswers();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech), answers, speech: speech);

        conversation.Ask("What's on my calendar tomorrow?", spoken: true);
        Pump();

        var question = conversation.Messages[0];
        Assert.True(question.IsSpoken);
        Assert.Equal("What's on my calendar tomorrow?", question.Text);
        Assert.Equal(1, conversation.Messages.Count(message => message.Role == MessageRole.Assistant));
        Assert.Same(conversation.Messages[1], Assert.Single(speech.Followed));
    });

    [Fact]
    public void ATypedRequestIsAnsweredInTextOnly() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var conversation = new ConversationViewModel(
            new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech), new StreamingAnswers(), speech: speech);

        conversation.Ask("What's on my calendar tomorrow?");
        Pump();

        Assert.False(conversation.Messages[0].IsSpoken);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Empty(speech.Followed);
    });

    [Fact]
    public void AskingAnythingNewSilencesWhatIsStillBeingSaid() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var conversation = new ConversationViewModel(
            new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech), new StreamingAnswers(), speech: speech);
        conversation.Ask("First question");
        Pump();
        speech.StopCount = 0;

        conversation.Ask("A typed follow-up");
        Pump();

        Assert.True(speech.StopCount >= 1);
    });

    [Fact]
    public void ASpokenRequestTakesTheFloorFromAnAnswerThatIsStillComing() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var answers = new StreamingAnswers { Hold = true };
        var input = new FakeVoiceInput();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone(), input, speech), answers, speech: speech);
        conversation.Ask("A long question");
        Pump();
        Assert.True(conversation.IsAnswering);
        conversation.Voice.Start();

        input.Listenings[0].Raise("never mind, what time is it", SpeechEndReason.Endpoint);
        Pump();

        Assert.Equal(2, answers.Questions.Count);
        Assert.Equal("Never mind, what time is it", answers.Questions[1]);
        Assert.Contains(conversation.Messages, message => message.IsSpoken && message.Text.StartsWith("Never mind", StringComparison.Ordinal));
    });

    [Fact]
    public void TheSpeakerButtonSilencesTheVoiceAndKeepsTheRestOfThatAnswerFromBeingSaid() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var answers = new StreamingAnswers { Hold = true };
        var conversation = new ConversationViewModel(
            new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech), answers, speech: speech);
        conversation.Ask("Tell me a long story", spoken: true);
        Pump();
        answers.Show();
        Pump();
        Assert.Single(speech.Followed);
        speech.IsSpeaking = true;
        Assert.True(conversation.SpeakerCommand.CanExecute(null));
        var answering = conversation.IsAnswering;

        conversation.SpeakerCommand.Execute(null);

        Assert.True(speech.StopCount >= 1);
        Assert.Equal(answering, conversation.IsAnswering);
        Assert.True(answers.StillWriting, "The speaker button must not stop the written answer.");

        // An answer to the same question that arrives later is not said either.
        speech.Followed.Clear();
        answers.ShowAnother();
        Pump();
        Assert.Empty(speech.Followed);
    });

    [Fact]
    public void TheSpeakerButtonReadsTheNewestAnswerWhenNothingIsBeingSaid() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var conversation = new ConversationViewModel(
            new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech), new StreamingAnswers(), speech: speech);
        Assert.False(conversation.SpeakerCommand.CanExecute(null));
        conversation.Ask("A typed question");
        Pump();
        Assert.True(conversation.SpeakerCommand.CanExecute(null));

        conversation.SpeakerCommand.Execute(null);

        Assert.Same(conversation.Messages[1], Assert.Single(speech.Followed));
    });

    [Fact]
    public void EscSilencesTheVoiceBeforeItClosesAnything() => RunSta(() =>
    {
        var speech = new FakeSpokenAnswers();
        var conversation = new ConversationViewModel(
            new VoiceInputViewModel(new FakeMicrophone(), new FakeVoiceInput(), speech), new StreamingAnswers(), speech: speech);
        conversation.Ask("A question", spoken: true);
        Pump();
        speech.IsSpeaking = true;
        speech.StopCount = 0;

        Assert.False(conversation.HandleEscape());
        Assert.Equal(1, speech.StopCount);
        speech.IsSpeaking = false;
        Assert.True(conversation.HandleEscape());
    });

    [Fact]
    public void TheKeyboardButtonPutsWhatWasHeardInTheComposer() => RunSta(() =>
    {
        var input = new FakeVoiceInput();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone(), input), new StreamingAnswers());
        conversation.Ask("First");
        Pump();
        conversation.Voice.Start();
        input.Listenings[0].Raise("and the second thing", null);
        Pump();

        conversation.UseKeyboardCommand.Execute(null);

        Assert.False(conversation.Voice.IsListening);
        Assert.Equal("And the second thing", conversation.Draft);
    });

    // ---- the window ----

    [Fact]
    public void TheOrbShowsOnlyWhileTheMicrophoneListensForSpeechAndFollowsItsLevel() => RunSta(() => WithTheme(() =>
    {
        var input = new FakeVoiceInput();
        var (window, bar, conversation) = CreateAssistant(voiceInput: input, speech: new FakeSpokenAnswers());
        try
        {
            window.Show();
            Pump();
            var orb = Named<AssistantOrb>(window, "BarOrb");
            Assert.Equal(Visibility.Collapsed, orb.Visibility);
            Assert.Equal(OrbState.Listening, orb.State);

            bar.Voice.Start();
            Pump();
            Assert.Equal(Visibility.Visible, orb.Visibility);
            Assert.NotNull(orb.AmplitudeSource);
            input.Listenings[0].Level = 0.2;
            Assert.True(orb.AmplitudeSource!.ReadAmplitude() > 0);

            bar.Voice.Stop();
            Pump();
            Assert.Equal(Visibility.Collapsed, orb.Visibility);
            Assert.False(Named<VoiceGlow>(window, "BarGlow").IsActive);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheWordsBeingHeardAreShownInTheBarAndInThePanel() => RunSta(() => WithTheme(() =>
    {
        var input = new FakeVoiceInput();
        var (window, bar, conversation) = CreateAssistant(voiceInput: input, speech: new FakeSpokenAnswers());
        try
        {
            window.Show();
            Pump();
            var field = Named<PromptInputControl>(window, "PromptInput");
            bar.Voice.Start();
            input.Listenings[0].Raise("open the", null);
            Pump();
            Assert.Equal("Open the", field.LiveText);
            Assert.True(field.HasLiveText);
            Assert.Equal("", field.Text);

            bar.Voice.Stop();
            Pump();
            Assert.Equal("", field.LiveText);

            conversation.Ask("A question");
            window.ShowConversation();
            Pump();
            conversation.Voice.Start();
            input.Listenings.Last().Raise("and then the settings", null);
            Pump();
            var transcript = Named<TextBlock>(window, "VoiceTranscript");
            Assert.Equal("And then the settings", transcript.Text);
            Assert.Equal(Visibility.Visible, transcript.Visibility);
            Assert.Equal(Visibility.Visible, Named<AssistantOrb>(window, "PanelOrb").Visibility);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheSpeakerButtonIsLitWhileTheAssistantSpeaksAndStopsItWhenPressed() => RunSta(() => WithTheme(() =>
    {
        var speech = new FakeSpokenAnswers();
        var (window, _, conversation) = CreateAssistant(answers: new StreamingAnswers(), speech: speech);
        try
        {
            conversation.Ask("A question", spoken: true);
            window.ShowConversation();
            Pump();
            var speaker = Named<Button>(window, "SpeakerButton");
            Assert.True(speaker.IsEnabled);
            var rest = speaker.Background;

            // The composer shows, so the plus has the speaker's place: until the Assistant speaks, when the speaker takes it back, lit.
            var plus = Named<Button>(window, "AddButton");
            Assert.Equal(Visibility.Collapsed, speaker.Visibility);
            Assert.Equal(Visibility.Visible, plus.Visibility);
            speech.IsSpeaking = true;
            Pump();
            Assert.Equal(Visibility.Visible, speaker.Visibility);
            Assert.Equal(Visibility.Collapsed, plus.Visibility);
            Assert.NotEqual(rest, speaker.Background);
            Assert.True(conversation.IsSpeaking);

            speaker.Command.Execute(null);
            Assert.True(speech.StopCount >= 1);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void DismissingTheWindowSilencesTheVoiceAtOnce() => RunSta(() => WithTheme(() =>
    {
        var speech = new FakeSpokenAnswers { IsSpeaking = true };
        var (window, _, _) = CreateAssistant(speech: speech);
        try
        {
            window.Show();
            Pump();
            speech.StopCount = 0;

            window.Dismiss();

            Assert.True(speech.StopCount >= 1, "The voice kept speaking after the window was dismissed.");
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheVoiceButtonOffersTheKeyboardWhileListeningAndItPutsTheWordsInTheComposer() => RunSta(() => WithTheme(() =>
    {
        var input = new FakeVoiceInput();
        var (window, _, conversation) = CreateAssistant(voiceInput: input, speech: new FakeSpokenAnswers());
        try
        {
            conversation.Ask("Something");
            window.ShowConversation();
            Pump();
            var voice = Named<Button>(window, "VoiceButton");
            voice.Command.Execute(null);
            Pump();
            Assert.True(conversation.Voice.IsListening);
            Assert.Equal("Use keyboard", AutomationName(voice));
            input.Listenings[0].Raise("typed by voice", null);
            Pump();

            voice.Command.Execute(null);
            Pump();

            Assert.False(conversation.Voice.IsListening);
            Assert.Equal("Typed by voice", conversation.Draft);
            Assert.Equal("Use microphone", AutomationName(voice));
        }
        finally { window.Close(); }
    }));

    // ---- the wake word, in the window controller ----

    [Fact]
    public void TheWakeWordOpensTheBarWhereAltAWouldAndStartsListeningWithWhatWasHeard() => RunSta(() =>
    {
        var shell = new WakeShell();
        var speech = new FakeSpokenAnswers { IsSpeaking = true };
        var input = new FakeVoiceInput();
        var (bar, conversation) = WakeParts(input, speech);
        var controller = new AssistantWindowStateController(shell, bar, conversation, speech: speech);
        var handoff = new WakeHandoff([5, 6, 7]);

        controller.BeginVoiceFromWakeWord(handoff);

        Assert.Equal(1, shell.ShowAndFocusCalls);
        Assert.True(speech.StopCount >= 1, "Saying Kiri must interrupt the voice.");
        Assert.True(bar.Voice.IsListening);
        Assert.False(conversation.Voice.IsListening);
        Assert.Same(handoff, input.Requests.Single().Handoff);
    });

    [Fact]
    public void TheWakeWordWhileTheBarIsUpListensWhereItIsWithoutOpeningAnotherWindow() => RunSta(() =>
    {
        var shell = new WakeShell();
        var input = new FakeVoiceInput();
        var (bar, conversation) = WakeParts(input, new FakeSpokenAnswers());
        var controller = new AssistantWindowStateController(shell, bar, conversation);
        controller.Invoke();
        Assert.Equal(1, shell.ShowAndFocusCalls);

        controller.BeginVoiceFromWakeWord(new WakeHandoff([1]));

        Assert.Equal(1, shell.ShowAndFocusCalls);
        Assert.Equal(1, shell.TakeForegroundCalls);
        Assert.True(bar.Voice.IsListening);
    });

    [Fact]
    public void TheWakeWordWhileTheConversationIsUpListensInTheConversation() => RunSta(() =>
    {
        var shell = new WakeShell();
        var input = new FakeVoiceInput();
        var (bar, conversation) = WakeParts(input, new FakeSpokenAnswers());
        var controller = new AssistantWindowStateController(shell, bar, conversation);
        controller.OpenNewConversation();
        shell.CurrentState = AssistantWindowState.FloatingConversation;

        controller.BeginVoiceFromWakeWord(new WakeHandoff([1]));

        Assert.True(conversation.Voice.IsListening);
        Assert.False(bar.Voice.IsListening);
    });

    [Fact]
    public void ABarTheWakeWordOpenedGoesAwayWhenNothingIsSaid() => RunSta(() =>
    {
        var shell = new WakeShell();
        var input = new FakeVoiceInput();
        var (bar, conversation) = WakeParts(input, new FakeSpokenAnswers());
        var controller = new AssistantWindowStateController(shell, bar, conversation);
        controller.BeginVoiceFromWakeWord(new WakeHandoff([1]));

        input.Listenings[0].Raise("", SpeechEndReason.NoSpeech);
        Pump();

        Assert.Equal(1, shell.DismissCalls);
    });

    [Fact]
    public void ABarTheUserOpenedStaysWhenTheWakeWordHeardNothingAfterIt() => RunSta(() =>
    {
        var shell = new WakeShell();
        var input = new FakeVoiceInput();
        var (bar, conversation) = WakeParts(input, new FakeSpokenAnswers());
        var controller = new AssistantWindowStateController(shell, bar, conversation);
        controller.Invoke();
        controller.BeginVoiceFromWakeWord(new WakeHandoff([1]));

        input.Listenings[0].Raise("", SpeechEndReason.NoSpeech);
        Pump();

        Assert.Equal(0, shell.DismissCalls);
    });

    [Fact]
    public void ASpokenRequestFromTheBarOpensTheConversationAndIsAnsweredAloud() => RunSta(() =>
    {
        var shell = new WakeShell();
        var speech = new FakeSpokenAnswers();
        var input = new FakeVoiceInput();
        var bar = new SearchOrAskViewModel(new VoiceInputViewModel(new FakeMicrophone(), input, speech));
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone(), input, speech), new StreamingAnswers(), speech: speech);
        _ = new AssistantWindowStateController(shell, bar, conversation, speech: speech);
        bar.Voice.Start();

        input.Listenings[0].Raise("what time is it", SpeechEndReason.Endpoint);
        Pump();

        Assert.Equal(1, shell.ExpandCalls);
        Assert.True(conversation.Messages[0].IsSpoken);
        Assert.Equal("What time is it?", conversation.Messages[0].Text);
        Assert.Single(speech.Followed);
    });

    [Fact]
    public void HidingTheAssistantSilencesTheVoice() => RunSta(() =>
    {
        var shell = new WakeShell();
        var speech = new FakeSpokenAnswers { IsSpeaking = true };
        var (bar, conversation) = WakeParts(new FakeVoiceInput(), speech);
        var controller = new AssistantWindowStateController(shell, bar, conversation, speech: speech);
        controller.Invoke();
        speech.StopCount = 0;

        shell.RaiseHidden();

        Assert.True(speech.StopCount >= 1);
    });

    private static (SearchOrAskViewModel Bar, ConversationViewModel Conversation) WakeParts(IVoiceInput input, FakeSpokenAnswers speech) =>
        (new SearchOrAskViewModel(new VoiceInputViewModel(new FakeMicrophone(), input, speech)),
            new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone(), input, speech), new StreamingAnswers(), speech: speech));

    // ---- fakes ----

    private sealed class FakeListening(Action<MicrophoneFailure> failed) : IVoiceListening
    {
        public double Level { get; set; }

        public bool FinishCalled { get; private set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler<SpeechTranscriptEventArgs>? Transcribed;

        public void Raise(string text, SpeechEndReason? ended) => Transcribed?.Invoke(this, new SpeechTranscriptEventArgs(text, ended));

        public void Fail(MicrophoneFailure failure) => failed(failure);

        public void Finish() => FinishCalled = true;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeVoiceInput : IVoiceInput
    {
        public List<FakeListening> Listenings { get; } = [];

        public List<VoiceListenRequest> Requests { get; } = [];

        public VoiceEngineStatus RecognizerStatus { get; set; } = new(VoiceEngineState.Ready);

        public bool Enabled { get; set; } = true;

        public string? Unavailable { get; set; }

        public bool IsEnabled => Enabled;

        public string? UnavailableMessage => Unavailable;

        public IVoiceListening Listen(VoiceListenRequest request, Action<MicrophoneFailure> failed)
        {
            Requests.Add(request);
            var listening = new FakeListening(failed);
            Listenings.Add(listening);
            return listening;
        }
    }

    private sealed class FakeSpokenAnswers : ISpokenAnswers
    {
        private bool _speaking;

        public List<MessageViewModel> Followed { get; } = [];

        public int StopCount { get; set; }

        public bool IsSpeaking
        {
            get => _speaking;
            set
            {
                if (_speaking != value)
                {
                    _speaking = value;
                    SpeakingChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler? SpeakingChanged;

        public void Follow(MessageViewModel answer) => Followed.Add(answer);

        public void Stop() => StopCount++;
    }

    // An answer provider whose answers a test releases: by default each question is answered at once with a text that is complete.
    private sealed class StreamingAnswers : IAnswerProvider
    {
        private Action<MessageViewModel>? _show;
        private TaskCompletionSource? _release;

        public bool Hold { get; set; }

        public bool StillWriting => _release is { Task.IsCompleted: false };

        public List<string> Questions { get; } = [];

        public MessageViewModel? Answer(string question) => null;

        public async Task StreamAnswerAsync(string question, Action<MessageViewModel> show, CancellationToken cancellationToken)
        {
            Questions.Add(question);
            _show = show;
            if (Hold)
            {
                _release = new TaskCompletionSource();
                try
                {
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            show(new MessageViewModel(MessageRole.Assistant, "An answer to " + question));
        }

        // The held answer shows its first words.
        public void Show() => _show?.Invoke(new MessageViewModel(MessageRole.Assistant, "The first words") { Status = MessageStatus.Answering });

        // The held answer shows this message.
        public void Show(MessageViewModel message) => _show?.Invoke(message);

        // Another answer arrives for the same question, such as the rest of a request that went on after a tool.
        public void ShowAnother() => _show?.Invoke(new MessageViewModel(MessageRole.Assistant, "More, later"));
    }

    private sealed class FakeSpokenResponse(FakeSpeech owner) : ISpokenResponse
    {
        private readonly System.Text.StringBuilder _said = new();

        public Guid Id { get; } = Guid.NewGuid();

        public string Said => _said.ToString();

        public bool Completed { get; private set; }

        public bool WasStopped { get; private set; }

        public bool IsStopped => WasStopped;

        public bool IsFinished => Completed || WasStopped;

        public TimeSpan? TimeToFirstAudio => null;

        public event EventHandler? Finished;

        public void Append(string? text)
        {
            if (!IsFinished)
            {
                _said.Append(text);
            }
        }

        public void Complete() => Completed = !WasStopped;

        public void Stop()
        {
            if (!IsFinished)
            {
                WasStopped = true;
                owner.Stopped(this);
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private sealed class FakeSpeech : ITextToSpeechService
    {
        public List<FakeSpokenResponse> Responses { get; } = [];

        public int StopAllCount { get; private set; }

        public TextToSpeechStatus Status { get; private set; } = new(TextToSpeechModels.DefaultId, new VoiceEngineStatus(VoiceEngineState.Ready));

        public bool IsSpeaking { get; private set; }

        public bool KeepLoaded { get; set; }

        public List<string> Selected { get; } = [];

        public Dictionary<string, TextToSpeechBenchmarkResult> Results { get; } = [];

        public event EventHandler? StatusChanged;

        public event EventHandler? SpeakingChanged;

        public Task SelectEngineAsync(string engineId, bool load = true, CancellationToken cancellationToken = default)
        {
            Selected.Add(engineId);
            SetStatus(new TextToSpeechStatus(engineId, new VoiceEngineStatus(VoiceEngineState.Ready, null, TimeSpan.FromMilliseconds(900))));
            return Task.CompletedTask;
        }

        public void SetStatus(TextToSpeechStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ISpokenResponse BeginResponse()
        {
            var response = new FakeSpokenResponse(this);
            Responses.Add(response);
            return response;
        }

        public void StopAll()
        {
            StopAllCount++;
            foreach (var response in Responses.Where(response => !response.IsFinished).ToList())
            {
                response.Stop();
            }
        }

        public Task<TextToSpeechBenchmarkResult> BenchmarkAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Results.TryGetValue(Status.EngineId, out var result) ? result : new TextToSpeechBenchmarkResult(Status.EngineId, null, []));

        public void SetSpeaking(bool speaking)
        {
            IsSpeaking = speaking;
            SpeakingChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stopped(FakeSpokenResponse response) => _ = response;

        public void Dispose()
        {
        }
    }

    // The Assistant's window as the controller sees it, for tests of what the controller does with it.
    private sealed class WakeShell : IAssistantWindow
    {
        public AssistantWindowState CurrentState { get; set; } = AssistantWindowState.Compact;

        public int ShowAndFocusCalls { get; private set; }

        public int TakeForegroundCalls { get; private set; }

        public int DismissCalls { get; private set; }

        public int ExpandCalls { get; private set; }

        public AssistantWindowState State => CurrentState;

        public ScreenPoint? SurfaceTop => null;

        public event EventHandler? Moved { add { } remove { } }

        public event EventHandler? Expanded;

        public event EventHandler? Hidden;

        public event EventHandler? AssistantStateChanged { add { } remove { } }

        public void RaiseHidden() => Hidden?.Invoke(this, EventArgs.Empty);

        public void ShowAndFocus(ScreenPoint? surfaceTop) => ShowAndFocusCalls++;

        public void ShowConversation(ScreenPoint? surfaceTop) => CurrentState = AssistantWindowState.FloatingConversation;

        public void ShowConversationNear(NearWindowTarget target) => CurrentState = AssistantWindowState.FloatingConversation;

        public bool TakeForeground()
        {
            TakeForegroundCalls++;
            return true;
        }

        public void ExpandToConversation()
        {
            ExpandCalls++;
            CurrentState = AssistantWindowState.FloatingConversation;
            Expanded?.Invoke(this, EventArgs.Empty);
        }

        public void Dismiss() => DismissCalls++;

        public bool HideNow() => true;
    }
}
