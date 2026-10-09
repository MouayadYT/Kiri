using Assistant.Core.Settings;
using Assistant.Windows.Audio;
using Xunit;

namespace Assistant.UI.Tests;

internal sealed class FakeMicrophones(params MicrophoneDevice[] devices) : IMicrophoneDevices
{
    public List<MicrophoneDevice> Devices { get; } = [.. devices];

    public IReadOnlyList<MicrophoneDevice> List() => [.. Devices];
}

public sealed partial class PromptInputControlTests
{
    private static readonly MicrophoneDevice UsbMicrophone = new("{usb}", "Microphone (USB Audio)", false);
    private static readonly MicrophoneDevice HeadsetMicrophone = new("{headset}", "Headset Microphone", true);

    [Fact]
    public void TheVoicePageOffersTheMicrophonesWindowsHas_AndRemembersTheOneChosen() => RunSta(() =>
    {
        var kit = CreateSettingsKit(microphones: new FakeMicrophones(HeadsetMicrophone, UsbMicrophone));
        var page = kit.Model.Voice;

        Assert.True(page.HasMicrophones);
        Assert.Equal(["Windows default", "Headset Microphone (default)", "Microphone (USB Audio)"], page.Microphones.Select(option => option.Name).ToArray());
        Assert.Null(page.SelectedMicrophone!.Id);

        page.SelectedMicrophone = page.Microphones[2];
        kit.Settle();

        Assert.Equal("{usb}", kit.Saved.Voice.MicrophoneDeviceId);

        page.SelectedMicrophone = page.Microphones[0];
        kit.Settle();

        Assert.Null(kit.Saved.Voice.MicrophoneDeviceId);
    });

    [Fact]
    public void AMicrophoneThatWasChosenAndIsUnpluggedIsShownAsNotConnected_AndTheChoiceIsKept() => RunSta(() =>
    {
        var saved = new AppSettings { Voice = new VoiceSettings { MicrophoneDeviceId = "{gone}" } };
        var devices = new FakeMicrophones(HeadsetMicrophone);
        var kit = CreateSettingsKit(saved, microphones: devices);
        var page = kit.Model.Voice;

        Assert.Equal("Chosen microphone (not connected)", page.SelectedMicrophone!.Name);
        Assert.True(page.SelectedMicrophone.IsMissing);
        Assert.Contains("not connected", page.MicrophoneNote, StringComparison.Ordinal);
        Assert.Equal("{gone}", kit.Saved.Voice.MicrophoneDeviceId);

        // Plugged in and looked for again: it is the choice again.
        devices.Devices.Add(new MicrophoneDevice("{gone}", "Studio Microphone", false));
        page.RefreshMicrophonesCommand.Execute(null);

        Assert.Equal("Studio Microphone", page.SelectedMicrophone!.Name);
        Assert.False(page.SelectedMicrophone.IsMissing);
        Assert.DoesNotContain("not connected", page.MicrophoneNote, StringComparison.Ordinal);
    });

    [Fact]
    public void WithNoMicrophoneListedThePageSaysSo_AndWithoutTheServiceItOffersNoChoice() => RunSta(() =>
    {
        var none = CreateSettingsKit(microphones: new FakeMicrophones());
        Assert.Contains("No microphone was found", none.Model.Voice.MicrophoneNote, StringComparison.Ordinal);

        Assert.False(CreateSettingsKit().Model.Voice.HasMicrophones);
    });

    [Fact]
    public void TheChoiceHolderKeepsOneDeviceOrNone()
    {
        var choice = new MicrophoneChoice();
        Assert.Null(choice.DeviceId);

        choice.DeviceId = "{usb}";
        Assert.Equal("{usb}", choice.DeviceId);

        choice.DeviceId = "  ";
        Assert.Null(choice.DeviceId);
    }
}
