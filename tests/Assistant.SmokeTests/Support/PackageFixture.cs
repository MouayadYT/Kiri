using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// The package the packaging checks work on: the one in <c>ASSISTANT_SMOKE_PACKAGE_DIR</c> (a package folder, <c>Assistant-&lt;version&gt;-win-x64</c>) when
/// that is set, so a package built for release is the one checked, and otherwise one built here with <c>packaging\Build-Package.ps1</c> in a folder of its own
/// (about a minute or two: the app and its helper programs published self-contained). Nothing in it is changed by a check.
/// </summary>
public sealed class PackageFixture : IAsyncLifetime
{
    private string? _built;

    /// <summary>The package folder.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>Whether this check built the package, with a model and a voice of its own (very small stand-ins), so the checks know it has both.</summary>
    public bool BuiltHere => _built is not null;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_SMOKE_PACKAGE_DIR") is { Length: > 0 } given)
        {
            Folder = given;
            Assert.True(Directory.Exists(Folder), "ASSISTANT_SMOKE_PACKAGE_DIR is not a folder: " + Folder);
            return;
        }

        _built = Path.Combine(Path.GetTempPath(), "asp-" + Guid.NewGuid().ToString("N")[..8]);

        // A model (a file that starts like a GGUF file) and a voice (a folder with a file in it): what the package builder needs to put both beside the program.
        var standIns = Path.Combine(_built, "stand-ins");
        Directory.CreateDirectory(Path.Combine(standIns, "voice"));
        var model = Path.Combine(standIns, "model.gguf");
        await File.WriteAllBytesAsync(model, [.. Encoding.ASCII.GetBytes("GGUF"), .. new byte[64]]);
        await File.WriteAllTextAsync(Path.Combine(standIns, "voice", "voice.onnx"), "a stand-in for a voice");

        var built = await Scripts.RunAsync(
            Repo.Combine("packaging", "Build-Package.ps1"), TimeSpan.FromMinutes(20), "-OutputDirectory", _built,
            "-Model", "chat-4b=" + model, "-Voice", "piper=" + Path.Combine(standIns, "voice"));
        Assert.True(built.ExitCode == 0, "The package was not built:\n" + built.Output);
        Folder = Directory.GetDirectories(_built, "Assistant-*-win-x64").Single();
    }

    public Task DisposeAsync()
    {
        if (_built is not null)
        {
            Delete(_built);
        }

        return Task.CompletedTask;
    }

    internal static void Delete(string folder)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Running the packaging scripts as a person does: in Windows PowerShell 5.1, with the execution policy their .cmd files set.</summary>
internal static class Scripts
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string script, TimeSpan limit, params string[] arguments)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult();
        process.StandardInput.Close();
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        // The script's own end is what is waited for, not the end of its output: a build leaves programs running (MSBuild's nodes) that keep the
        // output pipes open for minutes, and waiting for those pipes to close would wait for them.
        if (await Task.WhenAny(exited.Task, Task.Delay(limit)) != exited.Task)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(script)} did not finish in {limit.TotalMinutes:0} minutes. Output so far: {output}");
        }

        await Task.Delay(500);
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}
