using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Assets;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>Which text-to-speech engines are installed on the PC (step 123), from files on the PC alone.</summary>
public sealed class TextToSpeechAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-voice-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PackagedAssetPaths _paths;

    public TextToSpeechAssetsTests() => _paths = new PackagedAssetPaths(Path.Combine(_root, "install"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Install(params string[] engines)
    {
        Directory.CreateDirectory(_paths.VoicesDirectory);
        var groups = new List<AssetGroup>();
        foreach (var engine in engines)
        {
            var folder = Path.Combine(_paths.VoicesDirectory, engine);
            Directory.CreateDirectory(folder);
            var bytes = Encoding.UTF8.GetBytes("voice data of " + engine);
            File.WriteAllBytes(Path.Combine(folder, "voice.onnx"), bytes);
            groups.Add(new AssetGroup(engine, [new AssetFile("voice.onnx", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())]));
        }

        File.WriteAllText(_paths.ManifestOf(AssetKind.Voice), new AssetManifest(groups).ToJson());
    }

    private (TextToSpeechAssets Voices, PackagedAssets Assets) Create()
    {
        var assets = new PackagedAssets(_paths);
        return (new TextToSpeechAssets(assets), assets);
    }

    [Fact]
    public void WithNothingPackaged_EveryEngineIsNotInstalled_InTheOrderTheSettingsOfferThem()
    {
        var (voices, assets) = Create();
        using var _ = assets;

        var statuses = voices.Peek();

        Assert.Equal(TextToSpeechModels.All.Select(model => model.Id), statuses.Select(status => status.Model.Id));
        Assert.All(statuses, status =>
        {
            Assert.False(status.IsInstalled);
            Assert.False(status.IsAvailable);
            Assert.Equal("Not installed with this copy of the Assistant.", status.Description);
        });
    }

    [Fact]
    public async Task OnlyTheEnginesWhoseFilesArePackagedAndWhole_AreAvailable()
    {
        Install("kitten-tts-mini", "piper");
        File.WriteAllText(Path.Combine(_paths.VoicesDirectory, "piper", "voice.onnx"), "VOICE DATA OF PIPER");
        File.SetLastWriteTimeUtc(Path.Combine(_paths.VoicesDirectory, "piper", "voice.onnx"), DateTime.UtcNow.AddMinutes(1));
        var (voices, assets) = Create();
        using var _ = assets;

        var peeked = voices.Peek().ToDictionary(status => status.Model.Id);
        var verified = (await voices.VerifyAsync()).ToDictionary(status => status.Model.Id);

        Assert.True(peeked["kitten-tts-mini"].IsInstalled);
        Assert.False(peeked["kitten-tts-mini"].IsAvailable);
        Assert.Equal("Installed. Its files are checked before it is first used.", peeked["kitten-tts-mini"].Description);
        Assert.True(verified["kitten-tts-mini"].IsAvailable);
        Assert.Equal("Installed, and its files match what was packaged.", verified["kitten-tts-mini"].Description);
        Assert.False(verified["kokoro-82m-onnx"].IsInstalled);
        Assert.False(verified["piper"].IsAvailable);
        Assert.Equal(AssetGroupStatus.Damaged, verified["piper"].State.Status);
        Assert.Contains("do not match", verified["piper"].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineIsAskedForBeforeItIsUsed_AndRefusedWhenItIsNotAvailable()
    {
        Install("kokoro-82m-onnx");
        var (voices, assets) = Create();
        using var _ = assets;

        await voices.EnsureAvailableAsync("kokoro-82m-onnx");

        var missing = await Assert.ThrowsAsync<AssetIntegrityException>(() => voices.EnsureAvailableAsync("piper"));
        Assert.Equal(AssetKind.Voice, missing.Kind);
        Assert.Equal(AssetGroupStatus.NotPackaged, missing.State.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => voices.EnsureAvailableAsync("no-such-engine"));
    }

    [Fact]
    public void EachEnginesFolder_IsNamedForItsId_UnderTheVoicesFolder()
    {
        var (voices, assets) = Create();
        using var _ = assets;

        Assert.Equal(Path.Combine(_paths.VoicesDirectory, "piper"), voices.FolderOf(TextToSpeechModels.Piper));
        Assert.All(TextToSpeechModels.All, model => Assert.True(AssetManifestReader.IsValidGroupId(model.Id)));
    }

    [Fact]
    public void NoEngineNeedsAServer_TheStatusComesFromFilesAlone()
    {
        // The service is built from the packaged assets and nothing else: no address, no port and no process is in its contract.
        var constructor = typeof(TextToSpeechAssets).GetConstructors().Single();

        Assert.Equal([typeof(IPackagedAssets)], constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
