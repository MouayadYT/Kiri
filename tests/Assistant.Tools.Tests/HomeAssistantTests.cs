using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Home;
using Assistant.Core.Memory;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Assistant.Tools.Home;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The user's Home Assistant (PROJECT_SPEC §4.8): reached through its own REST API with the address and the long-lived token the user gave, with nothing
/// added to Home Assistant. Here a handler stands in for the server; the real one is tried by hand (RELEASE_CHECKLIST).
/// </summary>
public sealed class HomeAssistantServiceTests
{
    private const string Token = "token-the-user-pasted";

    /// <summary>A Home Assistant: its API answers its own token, lists what it has and does what its services do.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Down { get; set; }

        /// <summary>The only port it listens on, when set.</summary>
        public int? OnlyPort { get; set; }

        /// <summary>What it says at <c>/api/</c>; Home Assistant says a message.</summary>
        public string Hello { get; set; } = """{"message":"API running."}""";

        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        /// <summary>How many times a device is read back, after a service changed it, before it says so.</summary>
        public int ReadsBeforeEffect { get; set; }

        /// <summary>Takes a service call and never changes.</summary>
        public bool Stuck { get; set; }

        private (int Index, string State, int Reads)? _pending;

        public List<(string Id, string? Name, string State, string? Unit)> Things { get; } =
        [
            ("light.desk_lamp", "Desk Lamp", "off", null),
            ("switch.fan", "Window Fan", "off", null),
            ("sensor.bedroom_temperature", "Bedroom Temperature", "21.5", "°C"),
            ("switch.no_name", null, "on", null),
        ];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, body));
            if (Down || (OnlyPort is { } port && request.RequestUri.Port != port))
            {
                throw new HttpRequestException("Nothing is listening.");
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (path == "/api/")
            {
                return Json(Hello);
            }

            if (path == "/api/states")
            {
                return Json("[" + string.Join(',', Things.Select(Thing)) + """,{"entity_id":"Not An Id","state":"on"},{"state":"on"}]""");
            }

            if (path.StartsWith("/api/states/", StringComparison.Ordinal))
            {
                var index = Things.FindIndex(thing => thing.Id == path["/api/states/".Length..]);
                if (_pending is { } pending && pending.Index == index)
                {
                    // Like lights that take a moment to report their new state: it shows only after this many reads.
                    if (pending.Reads == 0)
                    {
                        Things[index] = Things[index] with { State = pending.State };
                        _pending = null;
                    }
                    else
                    {
                        _pending = pending with { Reads = pending.Reads - 1 };
                    }
                }

                return index < 0 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Thing(Things[index]));
            }

            if (request.Method == HttpMethod.Post && path.StartsWith("/api/services/", StringComparison.Ordinal))
            {
                var service = path[(path.LastIndexOf('/') + 1)..];
                var id = JsonDocument.Parse(body!).RootElement.GetProperty("entity_id").GetString();
                var index = Things.FindIndex(thing => thing.Id == id);
                if (index < 0 || service is not ("turn_on" or "turn_off"))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                var after = service == "turn_on" ? "on" : "off";
                if (!Stuck && ReadsBeforeEffect == 0)
                {
                    Things[index] = Things[index] with { State = after };
                }
                else if (!Stuck)
                {
                    _pending = (index, after, ReadsBeforeEffect);
                }

                return Json("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string Thing((string Id, string? Name, string State, string? Unit) thing) => JsonSerializer.Serialize(new
        {
            entity_id = thing.Id,
            state = thing.State,
            attributes = new Dictionary<string, string?> { ["friendly_name"] = thing.Name, ["unit_of_measurement"] = thing.Unit }
                .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value),
        });

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static HomeAssistantService Service(
        FakeServer server, FakeSecretStore? secrets = null, bool localOnly = false, TimeProvider? clock = null, IInstalledIntegrationRegistry? registry = null) =>
        new(TestSettings.LocalOnly(localOnly), secrets ?? new FakeSecretStore(), clock ?? TimeProvider.System, NullLogger<HomeAssistantService>.Instance, registry, server);

    [Theory]
    [InlineData("192.168.1.20:8123", "http://192.168.1.20:8123/")]
    [InlineData(" http://homeassistant.local:8123/api/mcp ", "http://homeassistant.local:8123/")]
    [InlineData("homeassistant:8123", "http://homeassistant:8123/")]
    [InlineData("https://home.example.com/lovelace/0", "https://home.example.com/")]
    [InlineData("http://localhost:8123", "http://localhost:8123/")]
    public void AnAddressIsKeptAsItsSchemeHostAndPort(string typed, string kept) =>
        Assert.Equal(kept, HomeAssistantService.Normalize(typed)?.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("home assistant")]
    [InlineData("ftp://192.168.1.20")]
    [InlineData("http://someone:secret@192.168.1.20:8123")]
    [InlineData("http://home.example.com:8123")] // A token is never sent over the internet in the clear.
    public void WhatIsNotAnAddressThatCanBeUsedIsNotKept(string typed) => Assert.Null(HomeAssistantService.Normalize(typed));

    [Fact]
    public async Task ConnectingAsksHomeAssistantFirst_AndKeepsTheAddressAndTheTokenOnlyWhenItAnswers()
    {
        var server = new FakeServer();
        var secrets = new FakeSecretStore();
        using var home = Service(server, secrets);
        var changed = 0;
        home.Changed += (_, _) => changed++;

        var status = await home.ConnectAsync("192.168.1.20:8123/", "Bearer " + Token);

        Assert.True(status.IsReady);
        Assert.Equal("http://192.168.1.20:8123", status.Address);
        Assert.Equal(4, status.Devices);
        Assert.True(home.IsConnected);
        Assert.Equal("http://192.168.1.20:8123", home.Address);
        Assert.Equal(1, changed);

        // Both are in the secret store, and nowhere else: the token without the word that was pasted before it.
        Assert.Equal("http://192.168.1.20:8123", secrets.Secrets[HomeAssistantService.AddressSecret]);
        Assert.Equal(Token, secrets.Secrets[HomeAssistantService.TokenSecret]);
        Assert.Equal(("GET", "/api/", null), server.Requests[0]);
    }

    [Fact]
    public async Task ATokenHomeAssistantDoesNotTake_AndAnAddressWhereNothingAnswers_AreSaidAndNothingIsKept()
    {
        var secrets = new FakeSecretStore();
        using var home = Service(new FakeServer(), secrets);
        Assert.Equal(HomeFailure.Unauthorized, (await home.ConnectAsync("http://192.168.1.20:8123", "another-token")).Failure);

        using var away = Service(new FakeServer { Down = true }, secrets);
        Assert.Equal(HomeFailure.Unreachable, (await away.ConnectAsync("http://192.168.1.20:8123", Token)).Failure);

        // Something answers there, but it is not Home Assistant.
        using var other = Service(new FakeServer { Hello = """{"status":"ok"}""" }, secrets);
        Assert.Equal(HomeFailure.Unreachable, (await other.ConnectAsync("http://192.168.1.20:8123", Token)).Failure);

        Assert.Equal(HomeFailure.Invalid, (await home.ConnectAsync("not an address", Token)).Failure);
        Assert.Equal(HomeFailure.Invalid, (await home.ConnectAsync("http://192.168.1.20:8123", "two words")).Failure);

        Assert.False(home.IsConnected);
        Assert.Empty(secrets.Secrets);
    }

    [Fact]
    public async Task AnAddressWithNoPortIsTriedAtHomeAssistantsOwn()
    {
        var server = new FakeServer { OnlyPort = HomeAssistantService.DefaultPort };
        using var home = Service(server);

        var status = await home.ConnectAsync("192.168.1.20", Token);

        Assert.True(status.IsReady);
        Assert.Equal("http://192.168.1.20:8123", status.Address);
    }

    [Fact]
    public async Task LocalOnlyKeepsTheAssistantFromAHomeAssistantOnTheInternet_AndNotFromOneOnTheUsersNetwork()
    {
        var server = new FakeServer();
        using var remote = Service(server, localOnly: true);
        Assert.Equal(HomeFailure.LocalOnly, (await remote.ConnectAsync("https://home.example.com", Token)).Failure);
        Assert.Empty(server.Requests);

        using var own = Service(server, localOnly: true);
        Assert.True((await own.ConnectAsync("http://homeassistant.local:8123", Token)).IsReady);
    }

    [Fact]
    public async Task WhatHomeAssistantListsIsReadWithItsNamesAndUnits_AndNotReadAgainAtOnce()
    {
        var server = new FakeServer();
        var clock = new ManualTimeProvider();
        using var home = Service(server, clock: clock);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        server.Requests.Clear();

        var listed = await home.GetDevicesAsync();

        Assert.Equal(HomeFailure.None, listed.Failure);
        Assert.Equal(["Desk Lamp", "Window Fan", "Bedroom Temperature", "no name"], listed.Devices.Select(device => device.Name));
        Assert.Equal(["light", "switch", "sensor", "switch"], listed.Devices.Select(device => device.Kind));
        Assert.Equal("°C", listed.Devices[2].Unit);
        Assert.Same(listed.Devices, home.Known);

        // It was read when it was connected, a moment ago: that list is still the list.
        Assert.Empty(server.Requests);
        clock.Advance(TimeSpan.FromMinutes(1));
        await home.GetDevicesAsync();
        Assert.Equal(("GET", "/api/states", null), Assert.Single(server.Requests));

        // A device's name is the user's own, and is not what the device prints as.
        Assert.DoesNotContain("Desk Lamp", listed.Devices[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceIsCalledForOneDeviceWithItsValues_AndWhatTheDeviceIsAfterwardsIsReadBack()
    {
        var server = new FakeServer();
        using var home = Service(server);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        server.Requests.Clear();

        var result = await home.CallAsync("light", "turn_on", "light.desk_lamp", new Dictionary<string, double> { ["brightness_pct"] = 40 });

        Assert.True(result.Done);
        Assert.Equal("on", result.State);
        Assert.Equal(("POST", "/api/services/light/turn_on", """{"entity_id":"light.desk_lamp","brightness_pct":40}"""), server.Requests[0]);
        Assert.Equal(("GET", "/api/states/light.desk_lamp", null), server.Requests[1]);
    }

    // Runs a call on a clock the test moves, from one wait to the next as soon as the call waits, and says how long it waited by that clock.
    private static async Task<(HomeCallResult Result, TimeSpan Waited)> OnTheClock(ManualTimeProvider clock, Func<Task<HomeCallResult>> call)
    {
        var started = clock.GetUtcNow();
        var task = call();
        for (var step = 0; !task.IsCompleted; step++)
        {
            Assert.True(step < 10_000, "The call never ended.");
            if (clock.NextDue is { } due)
            {
                clock.Advance(due - clock.GetUtcNow());
            }
            else
            {
                await Task.Delay(1);
            }
        }

        return (await task, clock.GetUtcNow() - started);
    }

    private static int StateReads(FakeServer server, string id) => server.Requests.Count(request => request.Path == "/api/states/" + id);

    [Fact]
    public async Task LightsThatTakeAMomentToSayTheyAreOff_AreWaitedFor_AndTheAnswerIsTheirNewState()
    {
        var server = new FakeServer();
        var clock = new ManualTimeProvider();
        using var home = Service(server, clock: clock);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        await OnTheClock(clock, () => home.CallAsync("light", "turn_on", "light.desk_lamp"));
        server.Requests.Clear();

        // The user's lights take about two seconds to say they changed.
        server.ReadsBeforeEffect = 4;
        var (result, waited) = await OnTheClock(clock, () => home.CallAsync("light", "turn_off", "light.desk_lamp", null, "off", CancellationToken.None));

        Assert.True(result.Done);
        Assert.Equal("off", result.State);
        Assert.Equal(5, StateReads(server, "light.desk_lamp"));
        Assert.Equal(TimeSpan.FromSeconds(2), waited);
    }

    [Fact]
    public async Task ALightThatStillSaysOn_IsWaitedForThreeSecondsAtMost_AndThenItsStateIsTheAnswer()
    {
        var server = new FakeServer();
        var clock = new ManualTimeProvider();
        using var home = Service(server, clock: clock);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        await OnTheClock(clock, () => home.CallAsync("light", "turn_on", "light.desk_lamp"));
        server.Requests.Clear();
        server.Stuck = true;

        var (result, waited) = await OnTheClock(clock, () => home.CallAsync("light", "turn_off", "light.desk_lamp", null, "off", CancellationToken.None));

        Assert.Equal("on", result.State);
        Assert.InRange(waited, TimeSpan.FromSeconds(2.5), HomeAssistantService.SettleLimit);
        Assert.True(StateReads(server, "light.desk_lamp") > 3, "It is read again while it is waited for.");
    }

    [Fact]
    public async Task WithoutAStateToWaitFor_OrForAThingThatIsNotOnOrOff_ItIsReadBackOnce()
    {
        var server = new FakeServer();
        server.Things.Add(("vacuum.downstairs", "Downstairs", "docked", null));
        var clock = new ManualTimeProvider();
        using var home = Service(server, clock: clock);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        server.Stuck = true;
        server.Requests.Clear();

        var (unexpected, waited) = await OnTheClock(clock, () => home.CallAsync("switch", "turn_on", "switch.fan", null, null, CancellationToken.None));
        Assert.Equal("off", unexpected.State);
        Assert.Equal(1, StateReads(server, "switch.fan"));
        Assert.True(waited < TimeSpan.FromSeconds(1));

        var (vacuum, _) = await OnTheClock(clock, () => home.CallAsync("vacuum", "turn_on", "vacuum.downstairs", null, "on", CancellationToken.None));
        Assert.Equal("docked", vacuum.State);
        Assert.Equal(1, StateReads(server, "vacuum.downstairs"));
    }

    [Fact]
    public async Task WhatHomeAssistantWillNotDoIsSaid_AndANameThatIsNotOneIsNeverSent()
    {
        var server = new FakeServer();
        using var home = Service(server);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        server.Requests.Clear();

        Assert.Equal(HomeFailure.Refused, (await home.CallAsync("light", "explode", "light.desk_lamp")).Failure);

        server.Requests.Clear();
        Assert.Equal(HomeFailure.Invalid, (await home.CallAsync("light/../..", "turn_on", "light.desk_lamp")).Failure);
        Assert.Equal(HomeFailure.Invalid, (await home.CallAsync("light", "turn_on", "light.desk lamp")).Failure);
        Assert.Equal(HomeFailure.Invalid, (await home.CallAsync("light", "turn_on", "light.desk_lamp", new Dictionary<string, double> { ["bad key"] = 1 })).Failure);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TheNextStartFindsTheHomeAssistantThatWasConnected()
    {
        var server = new FakeServer();
        var secrets = new FakeSecretStore();
        using (var first = Service(server, secrets))
        {
            await first.ConnectAsync("http://192.168.1.20:8123", Token);
        }

        using var next = Service(server, secrets);
        Assert.False(next.IsConnected);

        await next.LoadAsync();

        Assert.True(next.IsConnected);
        Assert.Equal(4, next.Known.Count);
        Assert.True((await next.CheckAsync()).IsReady);
    }

    [Fact]
    public async Task AHomeAssistantThatIsAwayIsNotWaitedForBeforeARequest_AndIsSaidToBeAway()
    {
        var server = new FakeServer();
        var secrets = new FakeSecretStore();
        using (var first = Service(server, secrets))
        {
            await first.ConnectAsync("http://192.168.1.20:8123", Token);
        }

        server.Down = true;
        using var next = Service(server, secrets);
        await next.LoadAsync();

        Assert.True(next.IsConnected);
        Assert.Empty(next.Known);
        Assert.Equal(HomeFailure.Unreachable, (await next.CheckAsync()).Failure);
        Assert.Equal(HomeFailure.Unreachable, (await next.CallAsync("light", "turn_on", "light.desk_lamp")).Failure);
    }

    [Fact]
    public async Task TheConnectionAnEarlierVersionMadeAsAnMcpServerIsTakenOver()
    {
        // 0.1.137 kept Home Assistant as an MCP server at /api/mcp with the pasted token, which most Home Assistants do not answer at.
        var earlier = Sample.Remote("homeassistant", "Home Assistant", "http://192.168.1.20:8123/api/mcp") with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.BearerToken,
                State = IntegrationAuthState.Ready,
                Secrets = [new IntegrationSecretBinding("Authorization", "homeassistant.token")],
            },
        };
        var secrets = new FakeSecretStore();
        secrets.Secrets["homeassistant.token"] = Token;
        var registry = new InstalledIntegrationRegistry(new MemoryIntegrationStore(earlier, Sample.Remote()), NullLogger<InstalledIntegrationRegistry>.Instance, secrets);
        var server = new FakeServer();
        using var home = Service(server, secrets, registry: registry);

        // Reading what is kept changes nothing: only the app that is starting takes the earlier connection over.
        await home.LoadAsync();
        Assert.False(home.IsConnected);
        Assert.Equal(2, (await registry.ListAsync()).Count);
        var changed = 0;
        home.Changed += (_, _) => changed++;

        await home.TakeOverEarlierAsync();

        Assert.True(home.IsConnected);
        Assert.Equal(1, changed);
        Assert.Equal("http://192.168.1.20:8123", home.Address);
        Assert.Equal(Token, secrets.Secrets[HomeAssistantService.TokenSecret]);
        Assert.True((await home.CheckAsync()).IsReady);

        // The MCP connection that could reach nothing is gone, with its copy of the token; the user's other connections are as they were.
        Assert.Equal(["todoist"], (await registry.ListAsync()).Select(record => record.Id));
        Assert.DoesNotContain("homeassistant.token", secrets.Secrets.Keys);
    }

    [Fact]
    public async Task DisconnectingForgetsTheAddressAndTheToken()
    {
        var secrets = new FakeSecretStore();
        using var home = Service(new FakeServer(), secrets);
        await home.ConnectAsync("http://192.168.1.20:8123", Token);
        var changed = 0;
        home.Changed += (_, _) => changed++;

        await home.DisconnectAsync();

        Assert.False(home.IsConnected);
        Assert.Null(home.Address);
        Assert.Empty(home.Known);
        Assert.Empty(secrets.Secrets);
        Assert.Equal(1, changed);
        Assert.Equal(HomeFailure.NotConnected, (await home.GetDevicesAsync()).Failure);
    }
}

/// <summary>
/// The home tools (PROJECT_SPEC §4.8): <c>control_home_device</c> does one thing to the one device the user named, after they say yes to it by its own
/// name, and <c>get_home_devices</c> reads what the devices are. Here Home Assistant is a recorder.
/// </summary>
public sealed class HomeToolsTests
{
    private sealed class RecordingHome : IHomeAssistant
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsConnected { get; set; } = true;

        public string? Address => IsConnected ? "http://192.168.1.20:8123" : null;

        public List<HomeDevice> Devices { get; } =
        [
            new("fan.fan", "Fan", "off"),
            new("switch.fan", "Window Fan", "off"),
            new("switch.fan_led", "Fan LED", "on"),
            new("light.desk_lamp", "Desk Lamp", "off"),
            new("light.bedroom_lamp", "Bedroom Lamp", "off"),
            new("light.bedroom_ceiling", "Bedroom Ceiling", "on"),
            new("climate.living_room", "Living Room", "heat"),
            new("cover.garage_door", "Garage Door", "closed"),
            new("lock.front_door", "Front Door", "locked"),
            new("scene.movie_night", "Movie Night", "scening"),
            new("vacuum.robot", "Robot", "docked"),
            new("switch.aquarium_pump", "Aquarium Pump", "unavailable"),
            new("sensor.bedroom_temperature", "Bedroom Temperature", "21.5", "°C"),
            new("sensor.bedroom_humidity", "Bedroom Humidity", "40", "%"),
        ];

        public IReadOnlyList<HomeDevice> Known => Devices;

        public HomeFailure Failure { get; set; }

        /// <summary>What a device is said to be after a call; <see langword="null"/> for what the service would make it.</summary>
        public string? StateAfter { get; set; }

        public List<string> Calls { get; } = [];

        public int Loads { get; private set; }

        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.CompletedTask;
        }

        public Task TakeOverEarlierAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<HomeStatus> ConnectAsync(string address, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HomeStatus> CheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HomeDevices> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Failure == HomeFailure.None ? new HomeDevices(HomeFailure.None, Devices) : HomeDevices.Failed(Failure));

        public Task<HomeCallResult> CallAsync(
            string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"{domain}.{service} {deviceId}" + (data is null ? string.Empty : " " + string.Join(',', data.Select(pair => $"{pair.Key}={pair.Value}"))));
            return Task.FromResult(new HomeCallResult(HomeFailure.None, StateAfter ?? (service.Contains("off", StringComparison.Ordinal) || service.Contains("return", StringComparison.Ordinal) ? "off" : "on")));
        }
    }

    private sealed class AllowAll : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, PermissionDecisionReason.Granted));
    }

    private static ToolCall Control(string arguments) => new("c1", ControlHomeDeviceTool.Name, arguments);

    private static ITool[] Tools(IHomeAssistant? home) => [new ControlHomeDeviceTool(home), new GetHomeDevicesTool(home)];

    private static (ToolExecutor Executor, FakeConfirmation Asked) Executor(IHomeAssistant home, bool confirm = true)
    {
        var asked = new FakeConfirmation(confirm);
        return (new ToolExecutor(Tools(home), asked, new AllowAll()), asked);
    }

    private static ToolContext About(string request, Guid? conversation = null) => new(conversation ?? Guid.NewGuid(), request);

    [Theory]
    [InlineData("turn on the fan")]
    [InlineData("dim the lights a little")]
    [InlineData("is the garage open?")] // A word for a thing in a home.
    [InlineData("start movie night")] // The name of one of the user's own devices.
    [InlineData("ask home assistant what is on")]
    public void TheToolsAreOfferedForARequestAboutTheHome(string request)
    {
        var home = new RecordingHome();
        Assert.All(Tools(home), tool => Assert.True(tool.IsOffered(About(request)), request));
    }

    [Theory]
    [InlineData("what is 2 plus 2")]
    [InlineData("find my tax return")]
    [InlineData("what is the temperature in Los Angeles")] // A sensor's name is not what a request is taken to be about the home by.
    public void TheyAreNotOfferedForAnyOtherRequest_NorWithoutAHomeAssistant(string request)
    {
        Assert.All(Tools(new RecordingHome()), tool => Assert.False(tool.IsOffered(About(request)), request));
        Assert.All(Tools(new RecordingHome { IsConnected = false }), tool => Assert.False(tool.IsOffered(About("turn on the fan"))));
        Assert.All(Tools(null), tool => Assert.False(tool.IsOffered(About("turn on the fan"))));
    }

    [Fact]
    public async Task AConversationThatBeganAboutTheHomeGoesOnBeingOne_AndTheDevicesAreReadBeforeARequest()
    {
        var home = new RecordingHome();
        var tools = Tools(home);
        var conversation = Guid.NewGuid();

        Assert.True(tools[0].IsOffered(About("turn on the desk lamp", conversation)));
        Assert.True(tools[0].IsOffered(About("and the other one too", conversation)));
        Assert.False(tools[0].IsOffered(About("and the other one too")));

        await tools[0].PrepareAsync(About("turn on the desk lamp"), CancellationToken.None);
        Assert.Equal(1, home.Loads);
    }

    [Theory]
    [InlineData("the fan", "Fan", "homeassistant.turn_on fan.fan")] // The name said exactly, and a fan before a switch.
    [InlineData("window fan", "Window Fan", "homeassistant.turn_on switch.fan")]
    [InlineData("my desk lamp please", "Desk Lamp", "homeassistant.turn_on light.desk_lamp")]
    [InlineData("desk lamps", "Desk Lamp", "homeassistant.turn_on light.desk_lamp")]
    [InlineData("robot", "Robot", "vacuum.start vacuum.robot")]
    [InlineData("movie night", "Movie Night", "scene.turn_on scene.movie_night")]
    public async Task TheDeviceTheUserNamedIsTheOneThatIsTurnedOn_AfterTheySayYesToItByName(string said, string device, string call)
    {
        var home = new RecordingHome();
        var (executor, asked) = Executor(home);

        var result = await executor.ExecuteAsync(Control(JsonSerializer.Serialize(new { device = said, action = "turn_on" })), About("turn it on"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([call], home.Calls);
        var shown = Assert.Single(asked.Shown);
        Assert.Equal(ConfirmationKind.ConnectedApp, shown.Kind);
        Assert.Equal($"Turn on {device}?", shown.Title);
        Assert.Equal("Turn on", shown.ApproveLabel);
        Assert.Contains(shown.Details, detail => detail.Label == "Device" && detail.Value == device);
        Assert.Contains($"{device} was turned on.", result.OutputJson, StringComparison.Ordinal);
        Assert.Contains("now shows it as on", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceTheUserDoesNotAllowIsNotChanged()
    {
        var home = new RecordingHome();
        var (executor, asked) = Executor(home, confirm: false);

        var result = await executor.ExecuteAsync(Control("""{"device":"window fan","action":"turn_on"}"""), About("turn on the window fan"));

        Assert.NotEqual(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, asked.Asked);
        Assert.Empty(home.Calls);
    }

    // ---- what a device is called, remembered ----

    private static (ToolExecutor Executor, FakeConfirmation Asked, InMemoryMemoryStore Memory) WithMemory(IHomeAssistant home)
    {
        var asked = new FakeConfirmation(true);
        var memory = new InMemoryMemoryStore();
        return (new ToolExecutor([new ControlHomeDeviceTool(home, memory), new GetHomeDevicesTool(home)], asked, new AllowAll()), asked, memory);
    }

    [Fact]
    public async Task TheAcIsTheOneThermostat_WithNobodyAskedWhichDevice()
    {
        var home = new RecordingHome();
        var (executor, _, memory) = WithMemory(home);

        var result = await executor.ExecuteAsync(Control("""{"device":"AC","action":"turn_off"}"""), About("turn off the AC"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["homeassistant.turn_off climate.living_room"], home.Calls);
        Assert.Empty(memory.Entries);
    }

    [Fact]
    public async Task WithTwoThermostatsTheUserIsAskedOnce_AndTheOneTheyChoseIsTheAcFromThenOn()
    {
        var home = new RecordingHome();
        home.Devices.Add(new HomeDevice("climate.bedroom", "Bedroom Thermostat", "cool"));
        var (executor, asked, memory) = WithMemory(home);
        var conversation = Guid.NewGuid();

        // "The AC" is a thermostat, and there are two: both are named, so the user can say which.
        var first = await executor.ExecuteAsync(Control("""{"device":"the AC","action":"turn_off"}"""), About("turn off the AC", conversation));
        Assert.Equal(ToolResultStatus.Failed, first.Status);
        Assert.Contains("Bedroom Thermostat", first.OutputJson, StringComparison.Ordinal);
        Assert.Contains("Living Room", first.OutputJson, StringComparison.Ordinal);
        Assert.Contains("if they say any of them will do, use the first", first.OutputJson, StringComparison.Ordinal);
        Assert.Empty(home.Calls);

        // They answer, and the device is turned off by its own name.
        var second = await executor.ExecuteAsync(Control("""{"device":"Bedroom Thermostat","action":"turn_off"}"""), About("the bedroom one", conversation));
        Assert.Equal(ToolResultStatus.Succeeded, second.Status);
        var kept = Assert.Single(memory.Entries);
        Assert.Equal((MemoryKind.HomeDevice, "ac", "climate.bedroom"), (kept.Kind, kept.Key, kept.Value));
        Assert.Equal("When you say \"the AC\" at home, you mean Bedroom Thermostat.", kept.Text);

        // In a later conversation "the AC" is that thermostat, with no question about which.
        home.Calls.Clear();
        var later = await executor.ExecuteAsync(Control("""{"device":"AC","action":"turn_on"}"""), About("turn the AC back on"));
        Assert.Equal(ToolResultStatus.Succeeded, later.Status);
        Assert.Equal(["homeassistant.turn_on climate.bedroom"], home.Calls);
        Assert.Equal("Turn on Bedroom Thermostat?", asked.Shown[^1].Title);
    }

    [Fact]
    public async Task ADeviceThatTheNameCouldNotHaveMeantIsNotRememberedForIt()
    {
        var home = new RecordingHome();
        home.Devices.Add(new HomeDevice("climate.bedroom", "Bedroom Thermostat", "cool"));
        var (executor, _, memory) = WithMemory(home);
        var conversation = Guid.NewGuid();

        await executor.ExecuteAsync(Control("""{"device":"the AC","action":"turn_off"}"""), About("turn off the AC", conversation));

        // The user moves on to something else: a lamp is no AC, and nothing is learned from it.
        var lamp = await executor.ExecuteAsync(Control("""{"device":"desk lamp","action":"turn_off"}"""), About("never mind, turn off the desk lamp", conversation));

        Assert.Equal(ToolResultStatus.Succeeded, lamp.Status);
        Assert.Empty(memory.Entries);
    }

    // ---- the answer to "which one?" ----

    private static HandlerTool Other(string name) => new(
        ToolDefinition.Create(name, "Does a thing.", [new ToolParameter("text", ToolParameterType.String, "Some text.")], RiskLevel.ReadOnly),
        (call, _, _, _) => Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}")));

    [Fact]
    public async Task AFewWordsThatNameOneOfTheDevicesAskedAboutAreTheAnswer_AndNothingElseIsOfferedBesideTheHomeTool()
    {
        var home = new RecordingHome();
        home.Devices.Add(new HomeDevice("climate.ac_ir_bridge", "AC IR Bridge", "off"));
        home.Devices.Add(new HomeDevice("climate.bedroom_ac", "Bedroom AC", "off"));
        var memory = new InMemoryMemoryStore();
        var control = new ControlHomeDeviceTool(home, memory);
        var registry = new ToolRegistry([control, new GetHomeDevicesTool(home), Other("search_files"), Other("read_file"), Other("open_application")]);
        var executor = new ToolExecutor([control], new FakeConfirmation(true), new AllowAll());
        var conversation = Guid.NewGuid();

        // Before anything is asked, a request about the home has every tool beside it.
        var asking = About("turn on the ac", conversation);
        Assert.False(control.Claims(asking));
        Assert.Contains("search_files", registry.ToolsFor(asking).Select(tool => tool.Name));

        // Two devices are called an AC: the user is asked which.
        var first = await executor.ExecuteAsync(Control("""{"device":"the ac","action":"turn_on"}"""), asking);
        Assert.Equal(ToolResultStatus.Failed, first.Status);
        Assert.Contains("AC IR Bridge", first.OutputJson, StringComparison.Ordinal);

        // They answer with a slip of the keyboard and stop the answer; then they answer again. It is the home tool's, and only that is offered:
        // the words are not something to look up in files.
        var answer = About("ac ir bridge", conversation);
        Assert.True(control.Claims(answer));
        Assert.Equal([ControlHomeDeviceTool.Name], registry.ToolsFor(answer).Select(tool => tool.Name));

        // The model gives the name that was asked about again: the user's own answer says which device it is.
        var second = await executor.ExecuteAsync(Control("""{"device":"the ac","action":"turn_on"}"""), answer);
        Assert.Equal(ToolResultStatus.Succeeded, second.Status);
        Assert.Equal(["homeassistant.turn_on climate.ac_ir_bridge"], home.Calls);
        Assert.Equal((MemoryKind.HomeDevice, "ac", "climate.ac_ir_bridge"), (memory.Entries[0].Kind, memory.Entries[0].Key, memory.Entries[0].Value));

        // The question is settled: the next request has every tool again.
        Assert.False(control.Claims(About("ac ir bridge", conversation)));
        Assert.Contains("search_files", registry.ToolsFor(About("find my tax return", conversation)).Select(tool => tool.Name));
    }

    [Theory]
    [InlineData("find my tax return")] // Something else altogether.
    [InlineData("the first one")] // No device is named: the model is left to it, with every tool.
    [InlineData("actually never mind that and tell me what the weather is like in the living room today please")] // Too long to be an answer.
    public async Task AMessageThatIsNotAnAnswerToWhichDeviceIsNotTheHomeToolsAlone(string reply)
    {
        var home = new RecordingHome();
        home.Devices.Add(new HomeDevice("climate.ac_ir_bridge", "AC IR Bridge", "off"));
        home.Devices.Add(new HomeDevice("climate.bedroom_ac", "Bedroom AC", "off"));
        var control = new ControlHomeDeviceTool(home, new InMemoryMemoryStore());
        var executor = new ToolExecutor([control], new FakeConfirmation(true), new AllowAll());
        var conversation = Guid.NewGuid();
        await executor.ExecuteAsync(Control("""{"device":"the ac","action":"turn_on"}"""), About("turn on the ac", conversation));

        Assert.False(control.Claims(About(reply, conversation)));

        // In another conversation the same words are no answer either.
        Assert.False(control.Claims(About("ac ir bridge")));
    }

    [Fact]
    public async Task ARememberedNameWhoseDeviceIsGoneIsLookedForAgain()
    {
        var home = new RecordingHome();
        var (executor, _, memory) = WithMemory(home);
        await memory.SaveAsync(new MemoryEntry(Guid.NewGuid(), MemoryKind.HomeDevice, "x", DateTimeOffset.UtcNow) { Key = "ac", Value = "climate.sold_with_the_house" });

        var result = await executor.ExecuteAsync(Control("""{"device":"AC","action":"turn_off"}"""), About("turn off the AC"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["homeassistant.turn_off climate.living_room"], home.Calls);
    }

    [Theory]
    [InlineData("bedroom", "Bedroom Ceiling", "Bedroom Lamp")] // Two that fit as well as each other: nobody is guessed.
    [InlineData("lamp", "Bedroom Lamp", "Desk Lamp")]
    public async Task ANameThatFitsMoreThanOneDeviceIsAskedAbout_AndNothingIsChanged(string said, string first, string second)
    {
        var home = new RecordingHome();
        var (executor, asked) = Executor(home);

        var result = await executor.ExecuteAsync(Control(JsonSerializer.Serialize(new { device = said, action = "turn_off" })), About("turn it off"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(first, result.OutputJson, StringComparison.Ordinal);
        Assert.Contains(second, result.OutputJson, StringComparison.Ordinal);
        Assert.Contains("Ask the user which one", result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(home.Calls);
    }

    [Theory]
    [InlineData("""{"device":"disco ball","action":"turn_on"}""", "No device called")] // Nothing is called that.
    [InlineData("""{"device":"fan","action":"lock"}""", "No device called")] // A fan is not something that is locked.
    [InlineData("""{"device":"aquarium pump","action":"turn_on"}""", "cannot reach")] // Home Assistant has lost it.
    [InlineData("""{"device":"desk lamp","action":"set_brightness"}""", "value")] // How bright was not said.
    [InlineData("""{"device":"desk lamp","action":"set_brightness","value":120}""", "value")]
    [InlineData("""{"device":"desk lamp","action":"paint"}""", "action")] // Not one of the things that can be done.
    [InlineData("""{"device":"","action":"turn_on"}""", "name")]
    public async Task WhatCannotBeDoneIsToldToTheModel_AndNobodyIsAsked(string arguments, string says)
    {
        var home = new RecordingHome();
        var (executor, asked) = Executor(home);

        var result = await executor.ExecuteAsync(Control(arguments), About("do something at home"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(says, result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(home.Calls);
    }

    [Theory]
    [InlineData("""{"device":"desk lamp","action":"set_brightness","value":40}""", "light.turn_on light.desk_lamp brightness_pct=40", "Set Desk Lamp to 40% brightness?")]
    [InlineData("""{"device":"living room","action":"set_temperature","value":22.5}""", "climate.set_temperature climate.living_room temperature=22.5", "Set Living Room to 22.5°?")]
    [InlineData("""{"device":"fan","action":"set_speed","value":50}""", "fan.set_percentage fan.fan percentage=50", "Set Fan to 50% speed?")]
    [InlineData("""{"device":"garage door","action":"open"}""", "cover.open_cover cover.garage_door", "Open Garage Door?")]
    [InlineData("""{"device":"front door","action":"unlock"}""", "lock.unlock lock.front_door", "Unlock Front Door?")]
    [InlineData("""{"device":"movie night","action":"run"}""", "scene.turn_on scene.movie_night", "Run Movie Night?")]
    [InlineData("""{"device":"robot","action":"turn_off"}""", "vacuum.return_to_base vacuum.robot", "Turn off Robot?")]
    [InlineData("""{"device":"Window Fan","action":"toggle"}""", "homeassistant.toggle switch.fan", "Switch Window Fan the other way?")]
    public async Task EachThingThatCanBeDoneIsHomeAssistantsOwnServiceForThatKindOfDevice(string arguments, string call, string question)
    {
        var home = new RecordingHome();
        var (executor, asked) = Executor(home);

        var result = await executor.ExecuteAsync(Control(arguments), About("do something at home"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([call], home.Calls);
        Assert.Equal(question, Assert.Single(asked.Shown).Title);
    }

    [Fact]
    public async Task ADeviceThatDidNotFollowIsNotSaidToBeDone()
    {
        var home = new RecordingHome { StateAfter = "off" };
        var (executor, _) = Executor(home);

        var result = await executor.ExecuteAsync(Control("""{"device":"window fan","action":"turn_on"}"""), About("turn on the window fan"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("still shows Window Fan as off", result.OutputJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HomeFailure.Unreachable, "did not answer")]
    [InlineData(HomeFailure.Unauthorized, "did not accept the access token")]
    [InlineData(HomeFailure.LocalOnly, "Local Only")]
    public async Task AHomeAssistantThatCannotBeUsedIsSaidInWordsTheModelCanPassOn(HomeFailure failure, string says)
    {
        var home = new RecordingHome { Failure = failure };
        var (executor, asked) = Executor(home);

        var result = await executor.ExecuteAsync(Control("""{"device":"window fan","action":"turn_on"}"""), About("turn on the window fan"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(says, result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);
    }

    [Fact]
    public async Task LookingAtTheDevicesOnlyReads_AndGivesWhatFitsWhatWasAskedFor()
    {
        var home = new RecordingHome();
        var (executor, asked) = Executor(home);

        var result = await executor.ExecuteAsync(new ToolCall("c1", GetHomeDevicesTool.Name, """{"device":"bedroom temperature"}"""), About("how warm is the bedroom"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(home.Calls);
        using var json = JsonDocument.Parse(result.OutputJson);
        var devices = json.RootElement.GetProperty("devices");

        // The one that has both words first, with its reading and its unit.
        Assert.Equal("Bedroom Temperature", devices[0].GetProperty("name").GetString());
        Assert.Equal("21.5 °C", devices[0].GetProperty("state").GetString());
        Assert.Contains(devices.EnumerateArray(), device => device.GetProperty("name").GetString() == "Bedroom Lamp");
        Assert.DoesNotContain(devices.EnumerateArray(), device => device.GetProperty("name").GetString() == "Garage Door");
    }

    [Fact]
    public async Task WithoutANameItListsWhatCanBeControlled_AndNeverMoreThanAModelCanRead()
    {
        var home = new RecordingHome();
        for (var index = 0; index < 60; index++)
        {
            home.Devices.Add(new HomeDevice($"light.hall_{index}", $"Hall {index}", "off"));
        }

        var (executor, _) = Executor(home);

        var result = await executor.ExecuteAsync(new ToolCall("c1", GetHomeDevicesTool.Name, "{}"), About("what lights do I have"));

        using var json = JsonDocument.Parse(result.OutputJson);
        var devices = json.RootElement.GetProperty("devices");
        Assert.Equal(GetHomeDevicesTool.MaxListed, devices.GetArrayLength());
        Assert.True(json.RootElement.GetProperty("not_listed").GetInt32() > 0);
        Assert.DoesNotContain(devices.EnumerateArray(), device => device.GetProperty("kind").GetString() == "sensor");
    }
}
