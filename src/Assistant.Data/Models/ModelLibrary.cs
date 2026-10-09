using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Assistant.Core.Models;
using Assistant.Core.Storage;
using SharpCompress.Compressors.BZip2;

namespace Assistant.Data.Models;

/// <summary>Resumable downloads stay in the cache until verified, then install in one directory move.</summary>
public sealed class ModelLibrary(AppPaths paths, HttpClient http) : IModelLibrary
{
    private const string Marker = ".kiri-installed";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FolderOf(DownloadableModel model)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(model.Id, "^[a-z0-9][a-z0-9-]{0,80}$") || model.Files.Any(file => Path.GetFileName(file.Name) != file.Name || file.Name.Contains(':')))
            throw new IOException("Invalid model identifier or filename.");
        return Path.Combine(model.Kind is DownloadKind.Voice or DownloadKind.SpeechRecognition or DownloadKind.WakeWord ? paths.VoicesDirectory : model.Kind == DownloadKind.Runtime ? paths.RuntimesDirectory : Path.Combine(paths.ModelsDirectory, "downloads"), model.Id);
    }

    public bool HasManagedFiles(DownloadableModel model) => File.Exists(Path.Combine(FolderOf(model), Marker));
    private static string InstallStamp(DownloadableModel model) => model.Id + ":" + string.Join(":", model.Files.Select(file => file.Sha256.ToLowerInvariant()));

    public bool IsInstalled(DownloadableModel model)
    {
        var folder = FolderOf(model);
        if (!File.Exists(Path.Combine(folder, Marker))) return false;
        var stamp = File.ReadAllText(Path.Combine(folder, Marker)).Trim();
        if (stamp != InstallStamp(model) && (stamp != model.Id || model.Id == "kokoro-82m-onnx")) return false;
        if (model.Kind == DownloadKind.Runtime) return model.ArchiveRoot == "@zip"
            ? Directory.Exists(Path.Combine(folder, "bin")) && Directory.EnumerateFiles(Path.Combine(folder, "bin"), "*.dll").Any()
            : File.Exists(Path.Combine(folder, "bin", "onnxruntime.dll")) && File.Exists(Path.Combine(folder, "bin", "onnxruntime_providers_cuda.dll"));
        if (model.Kind == DownloadKind.SpeechRecognition && model.ArchiveRoot is null) return model.Files.All(file => File.Exists(Path.Combine(folder, file.Name)) && new FileInfo(Path.Combine(folder, file.Name)).Length == file.Bytes);
        if (model.Kind is DownloadKind.SpeechRecognition or DownloadKind.WakeWord) return HasSpeechInputFiles(folder);
        return model.Kind == DownloadKind.Language
            ? model.Files.All(file => File.Exists(Path.Combine(folder, file.Name)) && new FileInfo(Path.Combine(folder, file.Name)).Length == file.Bytes)
            : Directory.EnumerateFiles(folder, "*.onnx").Any() && File.Exists(Path.Combine(folder, "tokens.txt")) && Directory.Exists(Path.Combine(folder, "espeak-ng-data"));
    }

    public async Task DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled(model)) return;
            var destination = FolderOf(model);
            EnsureOwnedPath(destination);
            var replacing = Directory.Exists(destination);
            if (replacing && !HasManagedFiles(model)) throw new IOException("This folder already contains model files. Move them before downloading this model.");
            if (replacing) CheckTree(destination);
            // A model that is installed but for a file the catalog has since given it (a vision projector) keeps what it has: only the rest is fetched.
            if (replacing && Kept(model, destination) is { Count: > 0 } kept) { await CompleteAsync(model, destination, kept, progress, cancellationToken).ConfigureAwait(false); return; }
            var cache = Path.Combine(paths.CacheDirectory, "model-downloads", model.Id);
            EnsureOwnedPath(cache);
            Directory.CreateDirectory(cache);
            var staging = Path.Combine(cache, "install");
            EnsureOwnedPath(staging);
            // Recover verified files if a prior install was interrupted during its final move.
            if (model.ArchiveRoot is null && Directory.Exists(staging))
                foreach (var file in model.Files)
                    if (!File.Exists(Path.Combine(cache, file.Name)) && File.Exists(Path.Combine(staging, file.Name))) File.Move(Path.Combine(staging, file.Name), Path.Combine(cache, file.Name));
            var missing = model.Files.Sum(file => Math.Max(0, file.Bytes - Size(Path.Combine(cache, file.Name))));
            var disk = new DriveInfo(Path.GetPathRoot(cache)!);
            if (disk.AvailableFreeSpace < missing + (model.ArchiveRoot is null ? 64_000_000 : model.Kind == DownloadKind.Runtime ? 3_000_000_000 : 1_000_000_000))
                throw new IOException("There is not enough free space for this download. Free some space and try again.");
            long completed = 0;
            foreach (var file in model.Files)
            {
                var target = Path.Combine(cache, file.Name);
                await ReceiveAsync(file, target, completed, model.DownloadBytes, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(completed + file.Bytes, model.DownloadBytes, "Verifying checksum…"));
                await using var input = File.OpenRead(target);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    input.Close();
                    File.Delete(target);
                    throw new IOException("The download did not pass its checksum. Please download it again.");
                }
                completed += file.Bytes;
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(completed, model.DownloadBytes, "Installing…"));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            if (model.ArchiveRoot is { } archiveRoot)
            {
                await Task.Run(() =>
                {
                    if (archiveRoot == "@zip") ExtractNvidia(Path.Combine(cache, model.Files[0].Name), staging, cancellationToken);
                    else ExtractVoice(Path.Combine(cache, model.Files[0].Name), staging, archiveRoot, cancellationToken, model.Kind == DownloadKind.Runtime, model.Kind is DownloadKind.SpeechRecognition or DownloadKind.WakeWord);
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var file in model.Files) File.Move(Path.Combine(cache, file.Name), Path.Combine(staging, file.Name));
            }
            await File.WriteAllTextAsync(Path.Combine(staging, Marker), InstallStamp(model), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var previous = Path.Combine(cache, "previous");
            EnsureOwnedPath(previous);
            if (Directory.Exists(previous)) { CheckTree(previous); Directory.Delete(previous, true); }
            if (replacing) Directory.Move(destination, previous);
            try { Directory.Move(staging, destination); }
            catch { if (replacing && !Directory.Exists(destination)) Directory.Move(previous, destination); throw; }
            try { Directory.Delete(cache, true); } catch (IOException) { /* Already installed; leftover cache is harmless. */ }
        }
        finally { _gate.Release(); }
    }

    public long BytesToDownload(DownloadableModel model)
    {
        if (IsInstalled(model)) return 0;
        var kept = Kept(model, FolderOf(model));
        return model.Files.Where(file => !kept.Contains(file)).Sum(file => file.Bytes);
    }

    // The files of a model that an earlier install put in its folder and checked: the marker keeps the checksum of each, and each is still as long as it was.
    private static IReadOnlyList<ModelDownloadFile> Kept(DownloadableModel model, string folder)
    {
        if (model.ArchiveRoot is not null || !File.Exists(Path.Combine(folder, Marker))) return [];
        var stamp = File.ReadAllText(Path.Combine(folder, Marker)).Trim().Split(':');
        if (stamp.Length < 2 || stamp[0] != model.Id) return [];
        return [.. model.Files.Where(file => stamp.Skip(1).Contains(file.Sha256.ToLowerInvariant()) && Size(Path.Combine(folder, file.Name)) == file.Bytes)];
    }

    // Adds what an installed model lacks beside what it has. The files it was installed with stay where they are, since the model may be loaded from
    // them; the new ones are downloaded to the cache, verified, and moved in, and only then does the marker say the model is whole.
    private async Task CompleteAsync(DownloadableModel model, string destination, IReadOnlyList<ModelDownloadFile> kept, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(paths.CacheDirectory, "model-downloads", model.Id);
        EnsureOwnedPath(cache);
        Directory.CreateDirectory(cache);
        var wanted = model.Files.Where(file => !kept.Contains(file)).ToList();
        var total = wanted.Sum(file => file.Bytes);
        var missing = wanted.Sum(file => Math.Max(0, file.Bytes - Size(Path.Combine(cache, file.Name))));
        if (new DriveInfo(Path.GetPathRoot(cache)!).AvailableFreeSpace < missing + 64_000_000)
            throw new IOException("There is not enough free space for this download. Free some space and try again.");
        long completed = 0;
        foreach (var file in wanted)
        {
            var target = Path.Combine(cache, file.Name);
            await ReceiveAsync(file, target, completed, total, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(completed + file.Bytes, total, "Verifying checksum…"));
            string hash;
            await using (var input = File.OpenRead(target)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new IOException("The download did not pass its checksum. Please download it again.");
            }
            completed += file.Bytes;
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(completed, total, "Installing…"));
        foreach (var file in wanted) File.Move(Path.Combine(cache, file.Name), Path.Combine(destination, file.Name), true);
        await File.WriteAllTextAsync(Path.Combine(destination, Marker), InstallStamp(model), CancellationToken.None).ConfigureAwait(false);
        try { Directory.Delete(cache, true); } catch (IOException) { /* Already installed; leftover cache is harmless. */ }
    }

    public async Task DeleteAsync(DownloadableModel model, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = FolderOf(model);
            EnsureOwnedPath(folder);
            if (!File.Exists(Path.Combine(folder, Marker))) throw new IOException("Only models downloaded by Kiri can be deleted here.");
            // Check the whole tree before deletion: a junction must never lead outside the model library.
            CheckTree(folder);
            await Task.Run(() => Directory.Delete(folder, true), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task ReceiveAsync(ModelDownloadFile file, string target, long completed, long total, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
    {
        long offset = Size(target);
        if (offset > file.Bytes) { File.Delete(target); offset = 0; }
        if (offset == file.Bytes) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange is not { From: { } from, Length: { } length } || from != offset || length != file.Bytes)
                throw new IOException("The server returned an invalid download range. Please try again.");
        }
        else offset = 0;
        await using var output = new FileStream(target, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[131072];
        var last = Environment.TickCount64;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var initialOffset = offset;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (offset + count > file.Bytes) throw new IOException("The download is larger than the verified model file.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            offset += count;
            if (Environment.TickCount64 - last > 100)
            {
                progress?.Report(new(completed + offset, total, "Downloading…", (offset - initialOffset) / Math.Max(0.001, started.Elapsed.TotalSeconds)));
                last = Environment.TickCount64;
            }
        }
        if (offset != file.Bytes) throw new IOException("The download was interrupted. Click Download to resume it.");
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    internal static void ExtractVoice(string archive, string destination, string root, CancellationToken token, bool runtime = false, bool speechInput = false)
    {
        using var input = File.OpenRead(archive);
        using var bz = BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, false);
        using var tar = new TarReader(bz);
        TarEntry? entry;
        long bytes = 0;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.Name.Replace('\\', '/');
            if (name == root || name == root + "/") continue;
            if (!name.StartsWith(root + "/", StringComparison.Ordinal)) throw new IOException("Unexpected voice archive layout.");
            var relative = name[(root.Length + 1)..];
            if (relative.Split('/').Any(part => part == ".." || part.Contains(':'))) throw new IOException("Invalid archive path.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid archive path.");
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new IOException("Links are not allowed in voice archives.");
            bytes += entry.Length;
            if (bytes > (runtime ? 3_000_000_000 : 1_000_000_000)) throw new IOException("The voice archive is larger than expected.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew);
            entry.DataStream?.CopyTo(output);
        }
        if (runtime)
        {
            if (!File.Exists(Path.Combine(destination, "bin", "onnxruntime.dll")) || !File.Exists(Path.Combine(destination, "bin", "onnxruntime_providers_cuda.dll"))) throw new IOException("The CUDA runtime download is incomplete.");
        }
        else if (speechInput && !HasSpeechInputFiles(destination)) throw new IOException("The speech input download is missing required files.");
        else if (!speechInput && (!Directory.EnumerateFiles(destination, "*.onnx").Any() || !File.Exists(Path.Combine(destination, "tokens.txt")) || !Directory.Exists(Path.Combine(destination, "espeak-ng-data"))))
            throw new IOException("The voice download is missing required files.");
    }

    private static bool HasSpeechInputFiles(string folder) => File.Exists(Path.Combine(folder, "tokens.txt")) &&
        (new[] { "encoder*.onnx", "decoder*.onnx", "joiner*.onnx" }.All(pattern => Directory.EnumerateFiles(folder, pattern).Any())
        || File.Exists(Path.Combine(folder, "model.int8.onnx"))
        || new[] { "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx" }.All(file => File.Exists(Path.Combine(folder, file))));

    private void EnsureOwnedPath(string path)
    {
        var root = Path.GetFullPath(paths.RootDirectory);
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid model location.");
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Model folders cannot contain links or junctions.");
            if (current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static void ExtractNvidia(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        Directory.CreateDirectory(Path.Combine(destination, "bin"));
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 || entry.FullName.Replace('\\', '/').Split('/').Any(part => part == ".." || part.Contains(':'))) throw new IOException("Invalid runtime archive path.");
            var name = entry.Name;
            var dll = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var license = name.Contains("license", StringComparison.OrdinalIgnoreCase) || name.Contains("eula", StringComparison.OrdinalIgnoreCase);
            if (!dll && !license || entry.Length == 0) continue;
            total += entry.Length; if (total > 3_000_000_000) throw new IOException("The runtime archive is too large.");
            var folder = Path.Combine(destination, dll ? "bin" : "licenses"); Directory.CreateDirectory(folder);
            using var input = entry.Open(); using var output = new FileStream(Path.Combine(folder, name), FileMode.CreateNew);
            input.CopyTo(output);
        }
        if (!Directory.EnumerateFiles(Path.Combine(destination, "bin"), "*.dll").Any()) throw new IOException("The runtime archive contains no native libraries.");
    }

    private static void CheckTree(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Model folders cannot contain links or junctions.");
            if ((attributes & FileAttributes.Directory) != 0) CheckTree(entry);
        }
    }
}
