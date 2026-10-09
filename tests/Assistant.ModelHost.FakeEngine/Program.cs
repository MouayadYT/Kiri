using System.Diagnostics;
using System.Runtime.InteropServices;
using Assistant.ModelHost.FakeEngine;

// Behaves like llama-server.exe as the model host starts it (--model <file> --host <socket>.sock ...), printing the
// lines the real server prints, private model paths among them.
var model = ValueOf("--model");
var socket = ValueOf("--host");
var clock = Stopwatch.StartNew();
var scenario = FakeEngineScenario.TryLoad(model);
var launch = scenario is null || model is null ? 0 : CountLaunch(model);

if (scenario is not null && model is not null)
{
    new FakeEngineReport(
        launch,
        Environment.ProcessId,
        args,
        NativeMethods.GetConsoleWindow() != IntPtr.Zero,
        Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("HF_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase))
            .ToArray(),
        Environment.CurrentDirectory).Save(model);

    if (scenario.StartupExitCode is { } silentExit)
    {
        return silentExit;
    }
}

Log('I', "srv  llama_server: initializing ...");
Log('I', "cmn  common_param: common_params_print_info: verbosity = 3 (adjust with the `-lv N` CLI arg)");
Log('I', "srv  init_listene: The UI is disabled");
Log('W', "srv  llama_server: security: no API key is set and CORS allows all origins (see https://github.com/ggml-org/llama.cpp/pull/25655)");

var onDevices = ValueOf("--device") is { } device && device != "none";
var failure = scenario is not null && launch >= scenario.StartupFailureFromLaunch
    && launch <= scenario.StartupFailureThroughLaunch
    && (!scenario.StartupFailureOnDevicesOnly || onDevices)
    ? scenario.StartupFailure
    : null;
if (failure == "bind")
{
    Log('E', $"srv         start: couldn't bind HTTP server socket, hostname: {socket}, port: 8080");
    Log('I', "srv    operator(): operator(): cleaning up before exit...");
    Log('E', "srv  llama_server: exiting due to HTTP server error");
    return 1;
}

// Like the real server, it creates its socket before it loads the model, and answers on it once it listens.
if (socket is not null && scenario is not null && model is not null)
{
    FakeChatServer.Listen(socket, model, scenario.Chat);
}
else if (socket is not null)
{
    File.WriteAllBytes(socket, []);
}

Log('I', $"srv    load_model: loading model '{model}'");
if (scenario is null || failure == "model")
{
    Log('E', $"gguf_init_from_file: failed to open GGUF file '{model}' (No such file or directory)");
    Log('E', $"llama_model_load: error loading model: llama_model_loader: failed to load model from {model}");
    Log('E', $"srv    load_model: failed to load model, '{model}'");
    Log('I', "srv    operator(): operator(): cleaning up before exit...");
    Log('E', "srv  llama_server: exiting due to model loading error");
    return 1;
}

Log('W', "load: bad special token: 'tokenizer.ggml.seperator_token_id' = 4294967295, using default id -1");
Log('I', "cmn          init: llama threadpool init, n_threads = 14", toOutput: true);
Log('I', "srv    load_model: initializing, n_slots = 4, n_ctx_slot = 2048, kv_unified = 'true'");
Thread.Sleep(scenario.ReadyDelayMs);
Log('I', "srv  llama_server: model loaded");
if (!scenario.NeverReady)
{
    Log('I', $"srv  llama_server: listening on unix://{socket}");
}

// What a verbose server would print about a request, private text included.
Log('I', "slot print_timing: id  3 | task 0 | prompt eval time =       1.11 ms /    22 tokens (    0.05 ms per token, 19855.60 tokens per second)", toOutput: true);
Log('I', "slot print_timing: id  3 | task 0 |        eval time =       5.08 ms /    16 tokens (    0.34 ms per token,  2950.43 tokens per second)");
Log('W', "srv  update_slots: PRIVATE-PROMPT-7f3c: summarize my tax letter");
Log('E', $"srv  PRIVATE-ANSWER-4d2e from {model}");

if (scenario.CrashingLaunches < 0 || launch <= scenario.CrashingLaunches)
{
    Thread.Sleep(scenario.CrashAfterMs);
    Log('E', "ggml_abort: GGML_ASSERT failed");
    return scenario.CrashExitCode;
}

Thread.Sleep(Timeout.Infinite);
return 0;

string? ValueOf(string option)
{
    var index = Array.IndexOf(args, option);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

void Log(char level, string message, bool toOutput = false)
{
    var elapsed = clock.Elapsed;
    var line = $"{(int)elapsed.TotalMinutes}.{elapsed.Seconds:00}.{elapsed.Milliseconds:000}.{elapsed.Microseconds:000} {level} {message}";
    (toOutput ? Console.Out : Console.Error).WriteLine(line);
}

static int CountLaunch(string scenarioPath)
{
    var countPath = FakeEngineReport.CountPath(scenarioPath);
    var count = File.Exists(countPath) ? int.Parse(File.ReadAllText(countPath), System.Globalization.CultureInfo.InvariantCulture) : 0;
    File.WriteAllText(countPath, (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    return count + 1;
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();
}
