using System.Text;
using Assistant.Core.Assets;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>Reading an asset manifest strictly (step 123): what it may name, and what it may never.</summary>
public sealed class AssetManifestTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static string Manifest(string groupId = "chat-4b", string path = "model.gguf", string size = "1234", string hash = Hash) =>
        $$"""{"schemaVersion":1,"groups":[{"id":"{{groupId}}","files":[{"path":"{{path}}","size":{{size}},"sha256":"{{hash}}"}]}]}""";

    [Fact]
    public void AManifest_IsReadIntoGroupsAndFiles()
    {
        var manifest = AssetManifestReader.Parse(Json(
            $$"""
            {"schemaVersion":1,"note":"ignored","groups":[
              {"id":"chat-4b","name":"Standard","files":[{"path":"model.gguf","size":10,"sha256":"{{Hash}}"},{"path":"extra/mmproj.gguf","size":0,"sha256":"{{Hash.ToUpperInvariant()}}"}]},
              {"id":"piper","files":[{"path":"voice.onnx","size":3,"sha256":"{{Hash}}"}]}]}
            """));

        Assert.Equal(["chat-4b", "piper"], manifest.Groups.Select(group => group.Id));
        var files = manifest.Find("chat-4b")!.Files;
        Assert.Equal(new AssetFile("model.gguf", 10, Hash), files[0]);
        Assert.Equal(new AssetFile("extra/mmproj.gguf", 0, Hash), files[1]);
        Assert.Null(manifest.Find("kokoro-82m-onnx"));
    }

    [Fact]
    public void AByteOrderMark_IsAccepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Json(Manifest())).ToArray();

        Assert.Single(AssetManifestReader.Parse(bytes).Groups);
    }

    [Fact]
    public void ToJson_WritesWhatParseReadsBack()
    {
        var manifest = new AssetManifest([
            new AssetGroup("chat-4b", [new AssetFile("model.gguf", 5, Hash), new AssetFile("mmproj.gguf", 6, Hash)]),
            new AssetGroup("piper", [new AssetFile("a/b.onnx", 7, Hash)]),
        ]);

        var read = AssetManifestReader.Parse(Json(manifest.ToJson()));

        Assert.Equal(
            manifest.Groups.SelectMany(group => group.Files.Select(file => (group.Id, file))),
            read.Groups.SelectMany(group => group.Files.Select(file => (group.Id, file))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"groups":[]}""")]
    [InlineData("""{"schemaVersion":2,"groups":[]}""")]
    [InlineData("""{"schemaVersion":"1","groups":[]}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"groups":{}}""")]
    [InlineData("""{"schemaVersion":1,"groups":[1]}""")]
    [InlineData("""{"schemaVersion":1,"groups":[{"id":"a"}]}""")]
    [InlineData("""{"schemaVersion":1,"groups":[{"id":"a","files":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"groups":[{"id":"a","files":[1]}]}""")]
    [InlineData("""{"schemaVersion":1,"groups":[]} trailing""")]
    [InlineData("""{"schemaVersion":1,/*c*/"groups":[]}""")]
    public void AManifestThatIsNotOneThisBuildReads_IsRefused(string text) =>
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(text)));

    [Theory]
    [InlineData("Chat-4b")]
    [InlineData("chat 4b")]
    [InlineData("-chat")]
    [InlineData("chat-")]
    [InlineData("chat_4b")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public void AGroupIdThatIsNotAPlainIdentifier_IsRefused(string id) =>
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(Manifest(groupId: id))));

    [Theory]
    [InlineData("../model.gguf")]
    [InlineData("a/../../model.gguf")]
    [InlineData("a/./model.gguf")]
    [InlineData("a//model.gguf")]
    [InlineData("/model.gguf")]
    [InlineData("\\\\\\\\server\\\\share\\\\model.gguf")]
    [InlineData("C:model.gguf")]
    [InlineData("C:\\\\model.gguf")]
    [InlineData("model.gguf:stream")]
    [InlineData("model.gguf.")]
    [InlineData("model.gguf ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a/COM1.gguf")]
    [InlineData("mo*del.gguf")]
    [InlineData("mo?del.gguf")]
    [InlineData("")]
    public void APathThatLeavesTheFolderOrIsNotAPlainName_IsRefused(string path) =>
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(Manifest(path: path))));

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"10\"")]
    [InlineData("99999999999999999999")]
    public void ASizeThatIsNotAWholeNumberOfBytes_IsRefused(string size) =>
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(Manifest(size: size))));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void AChecksumThatIsNotSha256Hexadecimal_IsRefused(string hash) =>
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(Manifest(hash: hash))));

    [Fact]
    public void ARepeatedGroupOrFile_IsRefused()
    {
        var file = $$"""{"path":"model.gguf","size":1,"sha256":"{{Hash}}"}""";
        var twiceGroup = $$"""{"schemaVersion":1,"groups":[{"id":"a","files":[{{file}}]},{"id":"a","files":[{{file}}]}]}""";
        var twiceFile = $$"""{"schemaVersion":1,"groups":[{"id":"a","files":[{{file}},{{file.Replace("model.gguf", "MODEL.gguf")}}]}]}""";

        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(twiceGroup)));
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(twiceFile)));
    }

    [Fact]
    public void TheLimits_AreEnforced()
    {
        var file = $$"""{"path":"f#N#.bin","size":1,"sha256":"{{Hash}}"}""";
        var tooManyFiles = string.Join(',', Enumerable.Range(0, AssetManifestReader.MaxFilesPerGroup + 1).Select(i => file.Replace("#N#", i.ToString())));
        var tooManyGroups = string.Join(',', Enumerable.Range(0, AssetManifestReader.MaxGroups + 1).Select(i => $$"""{"id":"g{{i}}","files":[{{file.Replace("#N#", "0")}}]}"""));

        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json($$"""{"schemaVersion":1,"groups":[{"id":"a","files":[{{tooManyFiles}}]}]}""")));
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json($$"""{"schemaVersion":1,"groups":[{{tooManyGroups}}]}""")));
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(new byte[AssetManifestReader.MaxBytes + 1]));
        Assert.Throws<AssetManifestException>(() => AssetManifestReader.Parse(Json(Manifest(path: new string('a', AssetManifestReader.MaxPathLength + 1)))));
        Assert.Single(AssetManifestReader.Parse(Json(Manifest(path: new string('a', AssetManifestReader.MaxPathLength)))).Groups);
    }

    [Fact]
    public void TheErrorMessages_NeverRepeatWhatTheManifestSaid()
    {
        var exception = Assert.Throws<AssetManifestException>(
            () => AssetManifestReader.Parse(Json(Manifest(path: "../PRIVATE-PATH-7f/model.gguf"))));

        Assert.DoesNotContain("PRIVATE", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("model.gguf", "model.gguf")]
    [InlineData("a\\b\\c.bin", "a/b/c.bin")]
    [InlineData("a/b.bin", "a/b.bin")]
    public void APathIsNormalizedToForwardSlashes(string path, string expected) =>
        Assert.Equal(expected, AssetManifestReader.NormalizePath(path));

    [Fact]
    public void ThePathsOfAKind_AreBesideTheProgramFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-install");
        var paths = new PackagedAssetPaths(root);

        Assert.Equal(Path.Combine(root, "assets", "models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(root, "assets", "voices"), paths.VoicesDirectory);
        Assert.Equal(Path.Combine(root, "assets", "models", "manifest.json"), paths.ManifestOf(AssetKind.Model));
        Assert.Equal(Path.Combine(root, "assets", "voices", "manifest.json"), paths.ManifestOf(AssetKind.Voice));
        Assert.Throws<ArgumentException>(() => new PackagedAssetPaths("relative"));
        Assert.Equal(AppContext.BaseDirectory.TrimEnd('\\', '/'), PackagedAssetPaths.ForCurrentProcess().InstallDirectory);
    }
}
