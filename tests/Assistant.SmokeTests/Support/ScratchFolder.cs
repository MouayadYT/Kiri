using System.IO;

namespace Assistant.SmokeTests.Support;

/// <summary>A folder in the temp directory that one check owns: its data folder, its documents, the programs it starts. Gone when the check is.</summary>
internal sealed class ScratchFolder : IDisposable
{
    // Short, because a UNIX socket's path (the model engine's) may not be longer than 107 bytes.
    public ScratchFolder() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asm-" + Guid.NewGuid().ToString("N")[..8]);

    public string Path { get; }

    /// <summary>A path inside the folder; the folder it is in is made.</summary>
    public string File(params string[] parts)
    {
        var path = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return path;
    }

    public void Dispose()
    {
        // A program the check started may still be letting go of its files.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
