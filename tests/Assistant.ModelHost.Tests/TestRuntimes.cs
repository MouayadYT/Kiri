using System.Diagnostics;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.ModelHost.Tests;

/// <summary>The llama.cpp runtime as the tests find it: bundled next to them, as it is next to the app.</summary>
internal static class TestRuntimes
{
    /// <summary>The bundled runtime's folder, copied here by the ModelHost project reference.</summary>
    public static string BundledDirectory => ModelRuntimeLayout.DefaultDirectory;

    /// <summary>A locator for the bundled runtime.</summary>
    public static IModelRuntimeLocator Bundled { get; } =
        new BundledRuntimeLocator(new ModelRuntimeOptions(BundledDirectory), NullLogger<BundledRuntimeLocator>.Instance);

    /// <summary>A new, short folder under the user's temp folder, which a UNIX socket's path can fit in.</summary>
    public static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asst-{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Deletes a folder a test made, retrying while a process that just ended still holds a file in it.</summary>
    public static void DeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Whether the process with <paramref name="processId"/> is still running.</summary>
    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Waits until the process with <paramref name="processId"/> has exited.</summary>
    public static async Task WaitUntilExitedAsync(int processId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (IsRunning(processId))
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

/// <summary>A locator that always finds what it was given.</summary>
internal sealed class StubRuntimeLocator(ModelRuntimeStatus status) : IModelRuntimeLocator
{
    public int Calls { get; private set; }

    public ModelRuntimeStatus Locate()
    {
        Calls++;
        return status;
    }
}
