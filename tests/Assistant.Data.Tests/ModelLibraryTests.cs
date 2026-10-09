using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Models;
using Assistant.Core.Storage;
using Assistant.Data.Models;
using SharpCompress.Compressors.BZip2;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class ModelLibraryTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "kiri-download-tests-" + Guid.NewGuid().ToString("N")));
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("A small, complete, verified model for a download test.");
    private static DownloadableModel Model(string? hash = null) => new("test-model", DownloadKind.Language, "Test", "Test", "1 GB",
        [new("model.gguf", "https://example.test/model", Content.Length, hash ?? Convert.ToHexString(SHA256.HashData(Content)))]);

    [Fact]
    public async Task UpdatingAManagedModelReplacesItsFilesOnlyAfterVerification()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        var library = new ModelLibrary(_paths, client);
        var model = Model();
        var folder = library.FolderOf(model);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".kiri-installed"), model.Id + ":old-checksum");
        File.WriteAllText(Path.Combine(folder, "model.gguf"), "Old model");
        Assert.True(library.HasManagedFiles(model));
        Assert.False(library.IsInstalled(model));
        await Assert.ThrowsAsync<IOException>(() => library.DownloadAsync(Model(new string('0', 64)), null));
        Assert.Equal("Old model", File.ReadAllText(Path.Combine(folder, "model.gguf")));
        await library.DownloadAsync(model, null);
        Assert.True(library.IsInstalled(model));
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(folder, "model.gguf")));
    }

    // The 4B models were first offered without a vision projector, so they could not read pictures. The catalog has since given them one: a model that
    // is already on the PC fetches the projector alone, and its weights (gigabytes, and perhaps loaded at that moment) stay where they are.
    [Fact]
    public async Task AModelInstalledBeforeItsProjectorWasOfferedFetchesOnlyTheProjector_AndKeepsItsWeights()
    {
        var projector = Encoding.UTF8.GetBytes("The part that reads pictures.");
        var before = Model();
        var after = before with { Files = [.. before.Files, new("projector.gguf", "https://example.test/projector", projector.Length, Convert.ToHexString(SHA256.HashData(projector)))] };
        var asked = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            asked.Add(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath == "/projector" ? projector : Content) };
        }));
        var library = new ModelLibrary(_paths, client);
        await library.DownloadAsync(before, null);
        asked.Clear();
        var weights = Path.Combine(library.FolderOf(after), "model.gguf");
        var written = File.GetLastWriteTimeUtc(weights);

        Assert.False(library.IsInstalled(after));
        Assert.True(library.HasManagedFiles(after));
        Assert.Equal(projector.Length, library.BytesToDownload(after));

        await library.DownloadAsync(after, null);

        Assert.Equal(["/projector"], asked);
        Assert.True(library.IsInstalled(after));
        Assert.Equal(0, library.BytesToDownload(after));
        Assert.Equal(projector, File.ReadAllBytes(Path.Combine(library.FolderOf(after), "projector.gguf")));
        Assert.Equal(Content, File.ReadAllBytes(weights));
        Assert.Equal(written, File.GetLastWriteTimeUtc(weights));
    }

    [Fact]
    public async Task AProjectorThatDoesNotPassItsChecksumLeavesTheModelAsItWas()
    {
        var before = Model();
        var after = before with { Files = [.. before.Files, new("projector.gguf", "https://example.test/projector", Content.Length, new string('0', 64))] };
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        var library = new ModelLibrary(_paths, client);
        await library.DownloadAsync(before, null);

        await Assert.ThrowsAsync<IOException>(() => library.DownloadAsync(after, null));

        Assert.True(library.IsInstalled(before));
        Assert.False(File.Exists(Path.Combine(library.FolderOf(after), "projector.gguf")));
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(library.FolderOf(after), "model.gguf")));
    }

    [Fact]
    public async Task VerifiedDownloadBecomesInstalledAndCanBeDeleted()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        var library = new ModelLibrary(_paths, client);
        var model = Model();
        Assert.False(library.IsInstalled(model));
        await library.DownloadAsync(model, null);
        Assert.True(library.IsInstalled(model));
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(library.FolderOf(model), "model.gguf")));
        await library.DeleteAsync(model);
        Assert.False(Directory.Exists(library.FolderOf(model)));
    }

    [Fact]
    public async Task CorruptedDownloadNeverAppearsAsInstalled()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        var library = new ModelLibrary(_paths, client);
        await Assert.ThrowsAsync<IOException>(() => library.DownloadAsync(Model(new string('0', 64)), null));
        Assert.False(Directory.Exists(library.FolderOf(Model())));
    }

    [Fact]
    public async Task SavedPartialDownloadResumesWithTheCorrectRange()
    {
        var cache = Path.Combine(_paths.CacheDirectory, "model-downloads", "test-model");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "model.gguf"), Content[..10]);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(10, Assert.Single(request.Headers.Range!.Ranges).From);
            var body = new ByteArrayContent(Content[10..]);
            body.Headers.ContentRange = new ContentRangeHeaderValue(10, Content.Length - 1, Content.Length);
            return new(HttpStatusCode.PartialContent) { Content = body };
        }));
        var library = new ModelLibrary(_paths, client);
        await library.DownloadAsync(Model(), null);
        Assert.True(library.IsInstalled(Model()));
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsWithoutAppendingDuplicateBytes()
    {
        var cache = Path.Combine(_paths.CacheDirectory, "model-downloads", "test-model");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "model.gguf"), Content[..10]);
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        var library = new ModelLibrary(_paths, client);
        await library.DownloadAsync(Model(), null);
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(library.FolderOf(Model()), "model.gguf")));
    }

    [Fact]
    public async Task CancellationDoesNotRegisterAnInstallation()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        var library = new ModelLibrary(_paths, client);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.DownloadAsync(Model(), null, cancel.Token));
        Assert.False(library.IsInstalled(Model()));
    }

    [Fact]
    public async Task UserSuppliedFolderIsNeverOverwrittenOrDeleted()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
        var library = new ModelLibrary(_paths, client);
        var folder = library.FolderOf(Model());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "user-model.gguf"); File.WriteAllText(file, "keep me");
        await Assert.ThrowsAsync<IOException>(() => library.DeleteAsync(Model()));
        await Assert.ThrowsAsync<IOException>(() => library.DownloadAsync(Model(), null));
        Assert.Equal("keep me", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("voice/../../outside.txt", TarEntryType.RegularFile)]
    [InlineData("voice/model.onnx", TarEntryType.SymbolicLink)]
    public void VoiceArchiveCannotWriteOutsideItsFolderOrCreateLinks(string name, TarEntryType type)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        var archive = Path.Combine(_paths.RootDirectory, "voice.tar.bz2");
        using (var file = File.Create(archive))
        using (var bz = BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Compress, false))
        using (var writer = new TarWriter(bz, leaveOpen: true))
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.SymbolicLink) entry.LinkName = "outside.txt";
            else entry.DataStream = new MemoryStream(Content);
            writer.WriteEntry(entry);
        }
        var destination = Path.Combine(_paths.RootDirectory, "extract"); Directory.CreateDirectory(destination);
        Assert.Throws<IOException>(() => ModelLibrary.ExtractVoice(archive, destination, "voice", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_paths.RootDirectory, "outside.txt")));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    public void Dispose() { if (Directory.Exists(_paths.RootDirectory)) Directory.Delete(_paths.RootDirectory, true); }
}
