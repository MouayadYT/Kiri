using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

public sealed class IntegrationStoreTests : IDisposable
{
    [Fact]
    public async Task PublicKeylessHeaderRoundTripsAndOlderRecordsWithoutHeadersRemainUsable()
    {
        var provider = Assistant.Tools.Search.HostedSearchProviders.Find(Assistant.Core.Settings.WebSearchProvider.Tavily);
        var record = Sample.Remote() with { Transport = provider.Transport };
        var store = Store();
        await store.SaveAsync([record]);
        var loaded = Assert.Single((await store.LoadAsync()).Integrations);
        Assert.Equal("keyless", loaded.Transport.Headers["X-Tavily-Access-Mode"]);
        var path = _folder.File("integrations.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        ((System.Text.Json.Nodes.JsonObject)json["integrations"]![0]!["transport"]!).Remove("headers");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var old = Assert.Single((await Store().LoadAsync()).Integrations);
        Assert.Empty(old.Transport.Headers);
    }
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private JsonInstalledIntegrationStore Store(string name = "integrations.json") => new(_folder.File(name));

    private static InstalledIntegration Full() => new()
    {
        Id = "googlecalendar",
        Name = "Google Calendar",
        Source = new IntegrationSource(IntegrationSourceKind.OfficialRegistry, "io.github.example/calendar"),
        Transport = new IntegrationTransport
        {
            Kind = McpTransportKind.Stdio,
            Command = @"C:\Program Files\nodejs\node.exe",
            Arguments = [@"C:\apps\calendar\index.js", "--stdio"],
            WorkingDirectory = @"C:\apps\calendar",
            Environment = new Dictionary<string, string> { ["REGION"] = "eu+west" },
        },
        InstalledVersion = "1.4.0+build.7",
        Enabled = true,
        Capabilities = new IntegrationCapabilities
        {
            Tools = true,
            Resources = true,
            ProtocolVersion = "2025-11-25",
            ToolNames = ["list_events", "createEvent"],
            RefreshedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        },
        Authentication = new IntegrationAuthentication
        {
            Kind = IntegrationAuthKind.EnvironmentSecret,
            State = IntegrationAuthState.Ready,
            Secrets = [new IntegrationSecretBinding("CALENDAR_TOKEN", "mcp.google-calendar.token")],
            GrantedScopes = ["calendar.read"],
            ExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        },
        Permissions = new IntegrationPermissions
        {
            RequiredCapability = PermissionCapability.Calendar,
            LeavesThisPc = true,
            AllowSideEffects = false,
            TrustToolAnnotations = true,
            ReadOnlyTools = ["list_events"],
            BlockedTools = ["delete_calendar"],
        },
        Health = new IntegrationHealth
        {
            Status = IntegrationHealthStatus.Unreachable,
            Failure = McpFailure.ConnectFailed,
            ConsecutiveFailures = 2,
            CheckedAt = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero),
        },
    };

    [Fact]
    public async Task ANewStoreIsEmpty()
    {
        var contents = await Store().LoadAsync();
        Assert.Empty(contents.Integrations);
        Assert.Equal(0, contents.Skipped);
        Assert.False(contents.Unreadable);
    }

    [Fact]
    public async Task AllThatIsKeptComesBackAsItWas()
    {
        var store = Store();
        await store.SaveAsync([Full(), Sample.Remote()]);

        var contents = await Store().LoadAsync();
        Assert.Equal(2, contents.Integrations.Count);
        var read = contents.Integrations[0];
        var written = Full();
        Assert.True(IntegrationJson.Equivalent(written, read));
        Assert.Equal(written.Transport.Environment["REGION"], read.Transport.Environment["REGION"]);
        Assert.Equal(["list_events", "createEvent"], read.Capabilities.ToolNames);
        Assert.Equal(PermissionCapability.Calendar, read.Permissions.RequiredCapability);
        Assert.Equal(McpFailure.ConnectFailed, read.Health.Failure);
        Assert.Equal("mcp.google-calendar.token", read.Authentication.Secrets[0].SecretName);
    }

    [Fact]
    public async Task TheFileHoldsOnlyTheNamesOfSecretsAndNeverAValue()
    {
        var path = _folder.File("integrations.json");
        await new JsonInstalledIntegrationStore(path).SaveAsync([Full()]);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("mcp.google-calendar.token", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("googlecalendar", document.RootElement.GetProperty("integrations")[0].GetProperty("id").GetString());
        Assert.Equal("stdio", document.RootElement.GetProperty("integrations")[0].GetProperty("transport").GetProperty("kind").GetString()!.ToLowerInvariant());
    }

    [Fact]
    public async Task TheFileIsUtf8WithoutAByteOrderMarkAndIsIndented()
    {
        var path = _folder.File("integrations.json");
        await new JsonInstalledIntegrationStore(path).SaveAsync([Sample.Remote()]);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Contains('\n', System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task APlusSignAndABackslashAreWrittenAsTheyAre()
    {
        var path = _folder.File("integrations.json");
        await new JsonInstalledIntegrationStore(path).SaveAsync([Full()]);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("1.4.0+build.7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASaveReplacesTheFileAllOrNothingAndLeavesNoTemporaryFile()
    {
        var path = _folder.File("integrations.json");
        var store = new JsonInstalledIntegrationStore(path);
        await store.SaveAsync([Sample.Remote("one", "One")]);
        await store.SaveAsync([Sample.Remote("two", "Two")]);

        var contents = await store.LoadAsync();
        Assert.Equal(["two"], contents.Integrations.Select(integration => integration.Id));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ASaveThatCannotBeWrittenFailsAndLeavesWhatWasThere()
    {
        var path = _folder.File("integrations.json");
        var store = new JsonInstalledIntegrationStore(path);
        await store.SaveAsync([Sample.Remote("one", "One")]);

        // A folder where the temporary file would go: the write cannot succeed.
        Directory.CreateDirectory(path + ".tmp");
        var exception = await Assert.ThrowsAsync<IntegrationException>(() => store.SaveAsync([Sample.Remote("two", "Two")]));
        Assert.Equal(IntegrationFailure.StoreFailed, exception.Failure);
        Directory.Delete(path + ".tmp");

        Assert.Equal(["one"], (await store.LoadAsync()).Integrations.Select(integration => integration.Id));
    }

    [Fact]
    public async Task AnEntryThatBreaksARuleIsLeftOutAndTheFileIsKeptAsItWas()
    {
        var path = _folder.File("integrations.json");
        var good = JsonSerializer.Serialize(Sample.Remote("good", "Good"), IntegrationJson.Options);
        var bad = JsonSerializer.Serialize(Sample.Remote("bad", "Bad"), IntegrationJson.Options).Replace("https://mcp.example.com/mcp", "http://evil.example.com/mcp", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"integrations":[{{good}},{{bad}},{"id":"broken"}]}""");

        var contents = await new JsonInstalledIntegrationStore(path).LoadAsync();

        Assert.Equal(["good"], contents.Integrations.Select(integration => integration.Id));
        Assert.Equal(2, contents.Skipped);
        Assert.True(File.Exists(_folder.File("integrations.skipped.json")));
        Assert.Contains("evil.example.com", await File.ReadAllTextAsync(_folder.File("integrations.skipped.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIdThatIsRepeatedIsReadOnce()
    {
        var path = _folder.File("integrations.json");
        var one = JsonSerializer.Serialize(Sample.Remote("same", "First"), IntegrationJson.Options);
        var two = JsonSerializer.Serialize(Sample.Remote("same", "Second"), IntegrationJson.Options);
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"integrations":[{{one}},{{two}}]}""");

        var contents = await new JsonInstalledIntegrationStore(path).LoadAsync();

        Assert.Equal("First", Assert.Single(contents.Integrations).Name);
        Assert.Equal(1, contents.Skipped);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public async Task AFileThatIsNotJsonIsReadAsEmptyAndKept(string text)
    {
        var path = _folder.File("integrations.json");
        await File.WriteAllTextAsync(path, text);

        var contents = await new JsonInstalledIntegrationStore(path).LoadAsync();

        Assert.Empty(contents.Integrations);
        Assert.True(contents.Unreadable);
        Assert.Equal(text, await File.ReadAllTextAsync(_folder.File("integrations.skipped.json")));
    }

    [Fact]
    public async Task AFileFromANewerBuildIsReadAsFarAsItIsUnderstoodAndCopiedFirst()
    {
        var path = _folder.File("integrations.json");
        var entry = JsonSerializer.Serialize(Sample.Remote(), IntegrationJson.Options);
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":7,"integrations":[{{entry}}],"somethingNew":true}""");

        var contents = await new JsonInstalledIntegrationStore(path).LoadAsync();

        Assert.Single(contents.Integrations);
        Assert.True(File.Exists(_folder.File("integrations.from-v7.json")));
    }

    [Fact]
    public async Task AnEntryWithAnUnknownEnumWordIsSkippedNotTheWholeFile()
    {
        var path = _folder.File("integrations.json");
        var good = JsonSerializer.Serialize(Sample.Remote("good", "Good"), IntegrationJson.Options);
        var odd = good.Replace("\"id\": \"good\"", "\"id\": \"odd\"", StringComparison.Ordinal).Replace("streamableHttp", "carrierPigeon", StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"integrations":[{{good}},{{odd}}]}""");

        var contents = await new JsonInstalledIntegrationStore(path).LoadAsync();

        Assert.Equal(["good"], contents.Integrations.Select(integration => integration.Id));
        Assert.Equal(1, contents.Skipped);
    }

    [Fact]
    public async Task AFileOfAnUnreasonableSizeIsNotRead()
    {
        var path = _folder.File("integrations.json");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(5 * 1024 * 1024);
        }

        var contents = await new JsonInstalledIntegrationStore(path).LoadAsync();

        Assert.True(contents.Unreadable);
        Assert.Empty(contents.Integrations);
    }

    [Fact]
    public async Task TheFolderIsMadeWhenItIsNotThere()
    {
        var path = Path.Combine(_folder.Path, "deeper", "still", "integrations.json");
        await new JsonInstalledIntegrationStore(path).SaveAsync([Sample.Remote()]);
        Assert.True(File.Exists(path));
    }
}
