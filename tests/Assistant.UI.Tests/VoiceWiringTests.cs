using System.Windows;
using Assistant.Core.Voice;
using Assistant.UI.Bootstrap;
using Assistant.UI.ViewModels;
using Assistant.UI.Voice;
using Assistant.Voice;
using Assistant.Windows.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

// How the app puts the voice together (PROJECT_SPEC §4.2, step 125): one microphone for the glow, the orb, speech recognition and the wake word, one voice
// for answers, and the same ones in the bar and in the conversation.
public sealed partial class PromptInputControlTests
{
    [Fact]
    public void TheAppBuildsTheVoiceOverTheRealServicesAndOneSharedMicrophone() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        using var host = AppHost.Create();
        try
        {
            var services = host.Services;

            // The level meter and the audio source are the one object, so the glow and speech recognition share one open microphone.
            var meter = services.GetRequiredService<IMicrophoneLevelMeter>();
            Assert.Same(meter, services.GetRequiredService<IMicrophoneAudioSource>());

            Assert.IsType<TextToSpeechService>(services.GetRequiredService<ITextToSpeechService>());
            Assert.IsType<SelectableSpeechToTextService>(services.GetRequiredService<ISpeechToTextService>());
            Assert.IsType<SherpaWakeWordService>(services.GetRequiredService<IWakeWordService>());
            Assert.IsType<WasapiAudioOutput>(services.GetRequiredService<IAudioOutput>());
            Assert.Same(services.GetRequiredService<MicrophoneRouter>(), services.GetRequiredService<IVoiceInput>());
            Assert.Same(services.GetRequiredService<ISpokenAnswers>(), services.GetRequiredService<ISpokenAnswers>());

            // The voice follows the settings from the start, and nothing listens until the user turns the wake word on.
            Assert.Contains(services.GetServices<IHostedService>(), service => service is VoiceRuntime);
            Assert.Same(services.GetRequiredService<IVoiceRuntime>(), services.GetServices<IHostedService>().OfType<VoiceRuntime>().Single());
            Assert.False(services.GetRequiredService<MicrophoneRouter>().IsListeningForWakeWord);

            // The bar's and the conversation's voice input recognize speech, and a microphone opened in one is never the other's.
            var bar = services.GetRequiredService<SearchOrAskViewModel>();
            var conversation = services.GetRequiredService<ConversationViewModel>();
            Assert.True(bar.Voice.RecognizesSpeech);
            Assert.True(conversation.Voice.RecognizesSpeech);
            Assert.NotSame(bar.Voice, conversation.Voice);
            Assert.NotNull(services.GetRequiredService<WakeWordController>());
        }
        finally
        {
            app.Resources.MergedDictionaries.Clear();
        }
    });
}
