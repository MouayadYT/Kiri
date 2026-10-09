using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Tools.FakeMcpServer;

// A fake MCP server over standard input and output. Arguments choose how it behaves:
//   --era modern|legacy|silent   which protocol it speaks (silent: legacy that ignores server/discover)
//   --legacy-version <version>   the newest legacy version it speaks
//   --page-size <n>              tools per page of tools/list
//   --noise                      writes lines that are not messages to its output, and text to its error stream
//   --stderr-flood               writes a megabyte to its error stream at the start
//   --ignore-close               does not end when its input closes
//   --exit-after <n>             ends after answering n requests
//   --log <file>                 appends the method of each message it receives to the file
//   --pid-file <file>            writes its process id to the file when it starts

var settings = new FakeMcpOptions();
string? log = null;
string? pidFile = null;
var noise = false;
var stderrFlood = false;
var ignoreClose = false;
var exitAfter = 0;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--era":
            settings.Era = args[++index] switch { "legacy" => FakeMcpEra.Legacy, "silent" => FakeMcpEra.LegacySilent, _ => FakeMcpEra.Modern };
            break;
        case "--legacy-version":
            settings.LegacyVersion = args[++index];
            break;
        case "--page-size":
            settings.PageSize = int.Parse(args[++index]);
            break;
        case "--noise":
            noise = true;
            break;
        case "--stderr-flood":
            stderrFlood = true;
            break;
        case "--ignore-close":
            ignoreClose = true;
            break;
        case "--exit-after":
            exitAfter = int.Parse(args[++index]);
            break;
        case "--log":
            log = args[++index];
            break;
        case "--pid-file":
            pidFile = args[++index];
            break;
    }
}

var core = new FakeMcpServerCore(settings)
{
    EnvironmentNames = () => Environment.GetEnvironmentVariables().Keys.Cast<string>(),
};
var output = Console.OpenStandardOutput();
var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var writeLock = new object();
var running = new ConcurrentDictionary<string, CancellationTokenSource>();
var answered = 0;

void Write(string text)
{
    lock (writeLock)
    {
        output.Write(Encoding.UTF8.GetBytes(text + "\n"));
        output.Flush();
    }
}

if (pidFile is not null)
{
    File.WriteAllText(pidFile, Environment.ProcessId.ToString());
}

if (stderrFlood)
{
    var error = Console.Error;
    var chunk = new string('e', 1000);
    for (var number = 0; number < 1000; number++)
    {
        error.WriteLine(chunk);
    }
}

if (noise)
{
    Write("this is not a message");
    Console.Error.WriteLine("starting up, with logging that is not a message");
}

while (input.ReadLine() is { } line)
{
    if (line.Length == 0)
    {
        continue;
    }

    JsonObject? message;
    try
    {
        message = JsonNode.Parse(line) as JsonObject;
    }
    catch (System.Text.Json.JsonException)
    {
        continue;
    }

    var method = (string?)message?["method"] ?? string.Empty;
    if (log is not null)
    {
        // Shared, so that a test reading the log while the program writes to it does not stop either.
        using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Encoding.UTF8.GetBytes(method + "\n"));
    }

    if (method == "notifications/cancelled" && message?["params"]?["requestId"] is { } requestId
        && running.TryGetValue(requestId.ToJsonString(), out var cancelled))
    {
        cancelled.Cancel();
        continue;
    }

    var key = message?["id"]?.ToJsonString();
    var cancellation = new CancellationTokenSource();
    if (key is not null)
    {
        running[key] = cancellation;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            if (noise)
            {
                Write("{not json");
            }

            var response = await core.HandleAsync(line, cancellation.Token).ConfigureAwait(false);
            if (response is not null && !cancellation.IsCancellationRequested)
            {
                Write(response);
                if (exitAfter > 0 && Interlocked.Increment(ref answered) >= exitAfter)
                {
                    Environment.Exit(0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A request that was cancelled is not answered.
        }
        finally
        {
            if (key is not null)
            {
                running.TryRemove(key, out _);
            }
        }
    });
}

if (ignoreClose)
{
    Thread.Sleep(Timeout.Infinite);
}
