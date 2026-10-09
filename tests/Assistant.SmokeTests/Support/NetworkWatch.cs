using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// What PROJECT_SPEC §3.4 promises, watched while a check runs: nothing reaches out of this PC and nothing listens. Two watchers. The first listens to
/// .NET's own network events in this process (every HTTP request and every name lookup, whoever makes it) and keeps any that is not for this PC. The
/// second reads Windows' table of network endpoints for this process and the programs it started (the model engine, the helper programs) and keeps any
/// that was not there before: a listening socket, a UDP socket, a connection to another machine. Only addresses and host names are kept, never the
/// content of a request.
/// </summary>
internal sealed class NetworkWatch : EventListener
{
    private static readonly Lazy<NetworkWatch> Shared = new(() => new NetworkWatch());

    private readonly object _gate = new();
    private readonly List<string> _requests = [];
    private readonly List<EventSource> _waiting = [];
    private bool _constructed;

    private NetworkWatch()
    {
        lock (_gate)
        {
            _constructed = true;
            foreach (var source in _waiting)
            {
                Enable(source);
            }

            _waiting.Clear();
        }
    }

    /// <summary>Starts watching, noting what is already there so that only what the check causes is held against it.</summary>
    public static Watching Start()
    {
        var watch = Shared.Value;
        lock (watch._gate)
        {
            return new Watching(watch, watch._requests.Count, Endpoints.Read());
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        lock (_gate)
        {
            // Called for the sources that exist while this is still being constructed: they are enabled when it has been.
            if (!_constructed)
            {
                _waiting.Add(eventSource);
                return;
            }
        }

        Enable(eventSource);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        // RequestStart(scheme, host, port, ...) of System.Net.Http; ResolutionStart(host) of System.Net.NameResolution.
        string? host;
        string? kind;
        if (data.EventSource.Name == "System.Net.Http" && data.EventName == "RequestStart" && data.Payload is { Count: >= 3 })
        {
            host = data.Payload[1]?.ToString();
            kind = $"HTTP request to {data.Payload[0]}://{host}:{data.Payload[2]}";
        }
        else if (data.EventSource.Name == "System.Net.NameResolution" && data.EventName == "ResolutionStart" && data.Payload is { Count: >= 1 })
        {
            host = data.Payload[0]?.ToString();
            kind = $"name lookup of {host}";
        }
        else
        {
            return;
        }

        if (IsThisPc(host))
        {
            return;
        }

        lock (_gate)
        {
            _requests.Add(kind);
        }
    }

    private void Enable(EventSource source)
    {
        if (source.Name is "System.Net.Http" or "System.Net.NameResolution")
        {
            EnableEvents(source, EventLevel.Informational, EventKeywords.All);
        }
    }

    private static bool IsThisPc(string? host) =>
        string.IsNullOrEmpty(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    /// <summary>One check's watch: it fails the check, with what was seen, when something left this PC or a socket opened.</summary>
    internal sealed class Watching(NetworkWatch watch, int requestsBefore, IReadOnlySet<string> endpointsBefore)
    {
        public void AssertSilent()
        {
            string[] requests;
            lock (watch._gate)
            {
                requests = [.. watch._requests.Skip(requestsBefore)];
            }

            var endpoints = Endpoints.Read().Except(endpointsBefore).Order().ToArray();
            Xunit.Assert.True(
                requests.Length == 0 && endpoints.Length == 0,
                "The check was not offline. Network use: " + string.Join("; ", requests.Concat(endpoints)));
        }
    }

    /// <summary>The network endpoints of this process and of every program below it, from Windows' own table (<c>netstat -ano</c>).</summary>
    private static class Endpoints
    {
        public static IReadOnlySet<string> Read()
        {
            var owners = new HashSet<int>(ProcessTree.Descendants().Select(program => program.Id)) { Environment.ProcessId };
            var found = new SortedSet<string>(StringComparer.Ordinal);
            var info = new ProcessStartInfo("netstat.exe", "-ano") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var netstat = Process.Start(info)!;
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var isTcp = parts.Length >= 5 && parts[0] == "TCP";
                var isUdp = parts.Length >= 4 && parts[0] == "UDP";
                if (!(isTcp || isUdp) || !int.TryParse(parts[^1], out var owner) || !owners.Contains(owner))
                {
                    continue;
                }

                var remote = isTcp ? parts[2] : "*:*";
                var state = isTcp ? parts[3] : "UDP";

                // A connection to this PC is the VSTest run talking to its console; a listener or a connection elsewhere is not.
                if (isUdp || state == "LISTENING" || !IsThisPc(remote))
                {
                    found.Add($"{parts[0]} {state} {parts[1]} -> {remote} (process {owner})");
                }
            }

            return found;
        }

        private static bool IsThisPc(string endpoint)
        {
            var colon = endpoint.LastIndexOf(':');
            var host = (colon >= 0 ? endpoint[..colon] : endpoint).Trim('[', ']').Split('%')[0];
            return host == "*" || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        }
    }
}
