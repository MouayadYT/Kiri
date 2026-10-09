using Assistant.Core.Contracts;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The small contracts around the settings: keys, secret names, and the default update.</summary>
public sealed class SettingsContractTests
{
    [Theory]
    [InlineData("a", "A")]
    [InlineData(" w ", "W")]
    [InlineData("7", "7")]
    [InlineData("f1", "F1")]
    [InlineData("F24", "F24")]
    [InlineData("space", "Space")]
    [InlineData("PAGEDOWN", "PageDown")]
    [InlineData("escape", "Escape")]
    public void KeyNamesAreWrittenTheOneWay(string name, string expected) => Assert.Equal(expected, HotkeyNames.Normalize(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("Ctrl")]
    [InlineData("é")]
    [InlineData("-")]
    public void ANameThatIsNoKeyIsRefused(string? name)
    {
        Assert.Null(HotkeyNames.Normalize(name));
        Assert.False(HotkeyNames.IsValid(name));
    }

    [Theory]
    [InlineData("github", true)]
    [InlineData("browser-bridge.token_1", true)]
    [InlineData("", false)]
    [InlineData("Upper", false)]
    [InlineData("has space", false)]
    [InlineData("slash/name", false)]
    [InlineData(null, false)]
    public void ASecretNameIsShortAndPlain(string? name, bool valid) => Assert.Equal(valid, SecretNames.IsValid(name));

    [Fact]
    public void ASecretNameOfSixtyFiveCharactersIsTooLong()
    {
        Assert.True(SecretNames.IsValid(new string('a', 64)));
        Assert.False(SecretNames.IsValid(new string('a', 65)));
    }

    [Fact]
    public void TheSettingsLimitsMatchTheEngineAndTheProfilesTheyAreWrittenOutFor()
    {
        // The settings depend on nothing else in Core, so they carry their own copy of these rules; these keep the copies right.
        Assert.Equal(ModelFiles.MinContextLength, SettingsLimits.MinContextTokens);
        Assert.Equal(ModelFiles.MaxContextLength, SettingsLimits.MaxContextTokens);
        foreach (var id in new[] { "chat-4b", "a", "a1", "-x", "x-", "", "Upper", "under_score", "has space", "ok-ok", "9", "--" })
        {
            Assert.Equal(ModelProfile.IsValidId(id), SettingsLimits.IsValidIdentifier(id));
        }

        Assert.False(SettingsLimits.IsValidIdentifier(null));
    }

    [Fact]
    public async Task ByDefaultAnUpdateIsALoadThenASave()
    {
        var settings = new FixedSettings();

        var updated = await ((ISettingsService)settings).UpdateAsync(current => current with { Ui = current.Ui with { BarResultsPerGroup = 6 } });

        Assert.Equal(6, updated.Ui.BarResultsPerGroup);
        Assert.Equal(6, settings.Current.Ui.BarResultsPerGroup);
    }

    [Fact]
    public void TheVoiceChoicesAreThePlannedThreeAndTheWakeWordIsKiri()
    {
        Assert.Equal(
            ["KittenTTS Mini 0.8 (80M)", "Kokoro-82M ONNX", "Piper"],
            TextToSpeechModels.All.Select(model => model.DisplayName));
        Assert.Equal(TextToSpeechModels.DefaultId, TextToSpeechModels.All[0].Id);
        Assert.Equal(TextToSpeechModels.All.Count, TextToSpeechModels.All.Select(model => model.Id).Distinct().Count());
        Assert.Equal("Kiri", VoiceSettings.WakeWord);
        Assert.False(new VoiceSettings().WakeWordEnabled);
        Assert.Null(TextToSpeechModels.Find("nope"));
    }
}
