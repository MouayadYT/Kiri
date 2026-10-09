using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 105: servers the user has already set up in other programs are found by name, and nothing else about them is read.</summary>
public sealed class LocalMcpConfigSourceTests : IDisposable
{
    private const string SecretToken = "sk-live-0123456789SECRET";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assistant-localmcp-" + Guid.NewGuid().ToString("N"));

    public LocalMcpConfigSourceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that cannot be removed is left to the system.
        }
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static LocalMcpConfigSource SourceFor(IPermissionPolicy? permissions, params LocalMcpConfigLocation[] locations) =>
        new(NullLogger<LocalMcpConfigSource>.Instance, permissions, locations);

    private static readonly FakePermissions Allowed = new(true);

    private static string Servers(string property = "mcpServers") => $$"""
        {
          "preferences": { "theme": "dark" },
          "{{property}}": {
            "microsoft-todo": { "command": "C:\\Tools\\node.exe", "args": ["C:\\x\\index.js", "--api-key={{SecretToken}}"], "env": { "MS_TOKEN": "{{SecretToken}}" } },
            "todoist": { "type": "http", "url": "https://ai.todoist.net/mcp?token={{SecretToken}}", "headers": { "Authorization": "Bearer {{SecretToken}}" } },
            "godot": { "command": "npx", "args": ["godot-mcp"] },
            "Notion-Legacy": { "type": "sse", "url": "https://mcp.notion.com/sse" }
          }
        }
        """;

    [Fact]
    public async Task AServerForTheAppIsFoundByItsNameAndNothingElseIsKept()
    {
        var path = Write("claude_desktop_config.json", Servers());
        var source = SourceFor(Allowed, new LocalMcpConfigLocation("Claude Desktop", path, ["mcpServers"]));

        var found = await source.FindAsync("microsofttodo");

        var server = Assert.Single(found);
        Assert.Equal(new AvailableIntegration("microsoft-todo", "Claude Desktop", McpTransportKind.Stdio), server);
        Assert.DoesNotContain(SecretToken, server.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("node.exe", server.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoteServerIsToldFromAProgramAndItsAddressIsNeverKept()
    {
        var path = Write("mcp.json", Servers());
        var source = SourceFor(Allowed, new LocalMcpConfigLocation("Cursor", path, ["mcpServers"]));

        var todoist = Assert.Single(await source.FindAsync("todoist"));
        var notion = Assert.Single(await source.FindAsync("notion"));

        Assert.Equal(McpTransportKind.StreamableHttp, todoist.Kind);
        Assert.Equal(McpTransportKind.LegacySse, notion.Kind);
        Assert.DoesNotContain("todoist.net", todoist.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretToken, todoist.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheAppsAskedAboutAreFound()
    {
        var path = Write("claude_desktop_config.json", Servers());
        var source = SourceFor(Allowed, new LocalMcpConfigLocation("Claude Desktop", path, ["mcpServers"]));

        Assert.Empty(await source.FindAsync("slack"));
        Assert.Empty(await source.FindAsync("github"));
        Assert.Equal(["godot"], (await source.FindAsync("godot")).Select(server => server.Name));
    }

    [Fact]
    public async Task EachProgramsOwnWordForItsServersIsRead()
    {
        var vscode = Write("vscode.json", Servers("servers"));
        var claude = Write("claude.json", Servers("mcpServers"));
        var source = SourceFor(
            Allowed,
            new LocalMcpConfigLocation("VS Code", vscode, ["servers"]),
            new LocalMcpConfigLocation("Claude Desktop", claude, ["mcpServers"]));

        var found = await source.FindAsync("todoist");

        Assert.Equal(["VS Code", "Claude Desktop"], found.Select(server => server.Where));
    }

    [Fact]
    public async Task ACommentedFileIsReadAsVsCodeWritesIt()
    {
        var path = Write("mcp.json", """
            {
              // Added by hand
              "servers": {
                "todoist": { "url": "https://example.com/mcp" }, /* the one I use */
              }
            }
            """);
        var source = SourceFor(Allowed, new LocalMcpConfigLocation("VS Code", path, ["servers"]));

        Assert.Single(await source.FindAsync("todoist"));
    }

    [Fact]
    public async Task WithoutTheFilesPermissionNothingIsRead()
    {
        var path = Write("claude_desktop_config.json", Servers());
        var location = new LocalMcpConfigLocation("Claude Desktop", path, ["mcpServers"]);

        Assert.Empty(await SourceFor(new FakePermissions(false), location).FindAsync("todoist"));
        Assert.Empty(await SourceFor(null, location).FindAsync("todoist"));
    }

    [Fact]
    public async Task APermissionThatIsTurnedOffIsAskedAboutAsFiles()
    {
        var permissions = new DenyingPermissions(PermissionCapability.Files);
        var path = Write("claude_desktop_config.json", Servers());

        await SourceFor(permissions, new LocalMcpConfigLocation("Claude Desktop", path, ["mcpServers"])).FindAsync("todoist");

        Assert.Equal([PermissionCapability.Files], permissions.Asked);
    }

    [Fact]
    public async Task AFileThatIsMissingNotJsonOrTooBigIsSkipped()
    {
        var notJson = Write("broken.json", "{ this is not json");
        var wrongShape = Write("array.json", "[1,2,3]");
        var huge = Write("huge.json", "{\"mcpServers\":{\"todoist\":{\"url\":\"https://x.example\"}},\"pad\":\"" + new string('x', 600 * 1024) + "\"}");
        var good = Write("good.json", Servers());
        var source = SourceFor(
            Allowed,
            new LocalMcpConfigLocation("A", Path.Combine(_folder, "missing.json"), ["mcpServers"]),
            new LocalMcpConfigLocation("B", notJson, ["mcpServers"]),
            new LocalMcpConfigLocation("C", wrongShape, ["mcpServers"]),
            new LocalMcpConfigLocation("D", huge, ["mcpServers"]),
            new LocalMcpConfigLocation("E", good, ["mcpServers"]));

        var found = await source.FindAsync("todoist");

        Assert.Equal("E", Assert.Single(found).Where);
    }

    [Fact]
    public async Task AServerNameIsCleanedBeforeItIsKept()
    {
        var path = Write("mcp.json", "{\"mcpServers\":{\"todoist\\u0007 ok\":{\"command\":\"x\"}}}");
        var found = await SourceFor(Allowed, new LocalMcpConfigLocation("Cursor", path, ["mcpServers"])).FindAsync("todoist");

        Assert.DoesNotContain(found, server => server.Name.Any(char.IsControl));
    }

    [Fact]
    public void TheUsualPlacesAreTheDedicatedFilesAndNotTheLargeStateFiles()
    {
        var places = LocalMcpConfigSource.DefaultLocations();

        Assert.Contains(places, place => place.Client == "Claude Desktop" && place.Path.EndsWith("claude_desktop_config.json", StringComparison.Ordinal));
        Assert.Contains(places, place => place.Client == "VS Code" && place.Path.EndsWith("mcp.json", StringComparison.Ordinal));
        Assert.Contains(places, place => place.Client == "Cursor");
        Assert.DoesNotContain(places, place => place.Path.EndsWith(".claude.json", StringComparison.Ordinal));
        Assert.All(places, place => Assert.True(Path.IsPathRooted(place.Path)));
    }

    [Fact]
    public async Task ALoggerThatSeesEverythingNeverSeesNamesOrPaths()
    {
        var logger = new CapturingLoggerFactory();
        var path = Write("claude_desktop_config.json", Servers());
        var source = new LocalMcpConfigSource(logger.CreateLogger<LocalMcpConfigSource>(), Allowed, [new LocalMcpConfigLocation("Claude Desktop", path, ["mcpServers"])]);

        await source.FindAsync("todoist");

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("todoist", StringComparison.OrdinalIgnoreCase) || line.Contains(_folder, StringComparison.OrdinalIgnoreCase));
    }
}
