using System.Security.Cryptography;
using Assistant.Core.Assets;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Assets made by the packaging script (packaging/Add-PackagedAssets.ps1, step 122) are read and checked by the app as they are: the script and the
/// reader are two implementations of one format, and this is what keeps them in step. The fixture holds a model with its projector and a voice engine
/// with files in folders, made by the script from tiny files.
/// </summary>
public sealed class ScriptMadeAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-script-assets-" + Guid.NewGuid().ToString("N"));
    private readonly PackagedAssetPaths _paths;

    public ScriptMadeAssetsTests()
    {
        _paths = new PackagedAssetPaths(Path.Combine(_root, "install"));
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "packaged-assets"), _paths.AssetsDirectory);
    }

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

    [Fact]
    public void TheManifestsTheScriptWrote_AreReadByTheApp()
    {
        var models = AssetManifestReader.Parse(File.ReadAllBytes(_paths.ManifestOf(AssetKind.Model)));
        var voices = AssetManifestReader.Parse(File.ReadAllBytes(_paths.ManifestOf(AssetKind.Voice)));

        Assert.Equal(["chat-4b"], models.Groups.Select(group => group.Id));
        Assert.Equal(["mmproj.gguf", "model.gguf"], models.Find("chat-4b")!.Files.Select(file => file.Path));
        Assert.Equal(["piper"], voices.Groups.Select(group => group.Id));
        Assert.Equal(["espeak-ng-data/voices/en.txt", "models/en_US-test.onnx.json", "piper.exe"], voices.Find("piper")!.Files.Select(file => file.Path));
    }

    [Fact]
    public void TheScriptsSizesAndChecksums_AreTheOnesTheAppComputes()
    {
        foreach (var kind in new[] { AssetKind.Model, AssetKind.Voice })
        {
            var manifest = AssetManifestReader.Parse(File.ReadAllBytes(_paths.ManifestOf(kind)));
            foreach (var group in manifest.Groups)
            {
                foreach (var file in group.Files)
                {
                    var path = Path.Combine(_paths.DirectoryOf(kind), group.Id, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    var bytes = File.ReadAllBytes(path);
                    Assert.Equal(bytes.Length, file.Size);
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Sha256);
                }
            }
        }
    }

    [Fact]
    public async Task TheAppVerifiesWhatTheScriptPackaged_AndNoticesAChange()
    {
        using var assets = new PackagedAssets(_paths);

        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Model, "chat-4b")).Status);
        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Voice, "piper")).Status);
        Assert.Equal(AssetGroupStatus.NotPackaged, (await assets.VerifyAsync(AssetKind.Voice, "kokoro-82m-onnx")).Status);

        File.WriteAllBytes(Path.Combine(_paths.ModelsDirectory, "chat-4b", "mmproj.gguf"), [71, 71, 85, 70, 3, 0, 0, 0, 9, 9, 8]);
        var damaged = await assets.VerifyAsync(AssetKind.Model, "chat-4b", AssetCheckMode.Reverify);

        Assert.Equal(AssetGroupStatus.Damaged, damaged.Status);
        Assert.Equal(["mmproj.gguf"], damaged.ProblemFiles);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
