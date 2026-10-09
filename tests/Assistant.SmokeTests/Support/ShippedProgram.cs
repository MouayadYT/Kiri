using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// One of the helper programs the app ships (the model host, the Explorer entry point, the browser bridge), copied alone into a folder of the check's own,
/// from the files its dependency list names. The programs that would start the app if they could not reach it look for it beside themselves, so a copy
/// with no app beside it can never start one: a check cannot open the real Assistant by mistake.
/// </summary>
internal sealed class ShippedProgram
{
    private ShippedProgram(string folder, string name)
    {
        Folder = folder;
        Name = name;
    }

    public string Folder { get; }

    public string Name { get; }

    public string Executable => Path.Combine(Folder, Name + ".exe");

    /// <summary>Copies <paramref name="name"/> (for example <c>Assistant.BrowserBridge</c>) and what it needs from the app's folder into <paramref name="scratch"/>.</summary>
    public static ShippedProgram CopyOf(ScratchFolder scratch, string name)
    {
        var from = AppContext.BaseDirectory;
        var folder = Path.Combine(scratch.Path, "p-" + name[(name.LastIndexOf('.') + 1)..]);
        Directory.CreateDirectory(folder);

        var wanted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            name + ".exe", name + ".dll", name + ".deps.json", name + ".runtimeconfig.json",
        };
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(from, name + ".deps.json")));
        foreach (var library in deps.RootElement.GetProperty("targets").EnumerateObject().First().Value.EnumerateObject())
        {
            foreach (var section in new[] { "runtime", "native" })
            {
                if (library.Value.TryGetProperty(section, out var files))
                {
                    wanted.UnionWith(files.EnumerateObject().Select(file => Path.GetFileName(file.Name)));
                }
            }
        }

        foreach (var file in wanted.Where(file => File.Exists(Path.Combine(from, file))))
        {
            File.Copy(Path.Combine(from, file), Path.Combine(folder, file), overwrite: true);
        }

        return new ShippedProgram(folder, name);
    }

    /// <summary>Starts it with its standard input and output redirected, as a browser starts the bridge.</summary>
    public Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("The program did not start.");
    }
}
