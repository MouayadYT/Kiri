using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

public sealed class IntegrationRulesTests
{
    [Fact]
    public void ARemoteIntegrationThatBreaksNoRuleHasNoProblems()
    {
        Assert.Empty(IntegrationRules.Problems(Sample.Remote()));
    }

    [Fact]
    public void AProgramThatBreaksNoRuleHasNoProblems()
    {
        Assert.Empty(IntegrationRules.Problems(Sample.Program(command: @"C:\Program Files\nodejs\node.exe", arguments: [@"C:\apps\server\index.js", "--stdio"])));
    }

    [Theory]
    [InlineData("todoist", true)]
    [InlineData("googlecalendar", true)]
    [InlineData("a", true)]
    [InlineData("a1b2", true)]
    [InlineData("google-calendar", false)]
    [InlineData("a1-b2", false)]
    [InlineData("", false)]
    [InlineData("Todoist", false)]
    [InlineData("1todoist", false)]
    [InlineData("-todoist", false)]
    [InlineData("todoist-", false)]
    [InlineData("to--doist", false)]
    [InlineData("to_doist", false)]
    [InlineData("to doist", false)]
    [InlineData("a-very-long-id-that-goes-on-and-on-and-on-and-on", false)]
    public void IdsAreLowerCaseLettersAndDigitsStartingWithALetter(string id, bool valid)
    {
        Assert.Equal(valid, IntegrationRules.IsValidId(id));
        Assert.Equal(valid, IntegrationRules.Problems(Sample.Remote(id)).Count == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameCannotBeEmpty(string name) => Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote(name: name)));

    [Fact]
    public void ANameCannotHoldControlCharactersOrBeTooLong()
    {
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote(name: "Todo\nist")));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote(name: new string('n', IntegrationRules.MaxNameLength + 1))));
    }

    [Theory]
    [InlineData("https://mcp.example.com/mcp", true)]
    [InlineData("https://mcp.example.com:8443/server/mcp?x=1", true)]
    [InlineData("http://localhost:3000/mcp", true)]
    [InlineData("http://127.0.0.1:9/mcp", true)]
    [InlineData("http://[::1]:9/mcp", true)]
    [InlineData("http://app.localhost/mcp", true)]
    [InlineData("http://mcp.example.com/mcp", false)]
    [InlineData("http://192.168.1.20/mcp", true)]
    [InlineData("http://10.0.0.7:8123/api/mcp", true)]
    [InlineData("http://172.20.1.4/mcp", true)]
    [InlineData("http://homeassistant.local:8123/api/mcp", true)]
    [InlineData("http://homeassistant:8123/api/mcp", true)]
    [InlineData("http://[fd12:3456:789a::1]:8123/api/mcp", true)]
    [InlineData("http://8.8.8.8/mcp", false)]
    [InlineData("http://172.32.0.1/mcp", false)]
    [InlineData("http://192.169.1.20/mcp", false)]
    [InlineData("http://local.example.com/mcp", false)]
    [InlineData("ftp://mcp.example.com/mcp", false)]
    [InlineData("file:///C:/mcp", false)]
    [InlineData("mcp.example.com/mcp", false)]
    [InlineData("https://user:pass@mcp.example.com/mcp", false)]
    [InlineData("https://mcp.example.com/mcp#section", false)]
    [InlineData("https://mcp.example.com/m cp", false)]
    [InlineData("", false)]
    public void AnAddressIsHttpsOrHttpOnThisPcOrTheUsersOwnNetwork(string endpoint, bool valid)
    {
        Assert.Equal(valid, McpEndpointRules.Problem(endpoint) is null);
        Assert.Equal(valid, IntegrationRules.Problems(Sample.Remote(endpoint: endpoint)).Count == 0);
    }

    [Fact]
    public void AnAddressThatIsTooLongIsRefused()
    {
        Assert.NotNull(McpEndpointRules.Problem("https://example.com/" + new string('a', McpEndpointRules.MaxLength)));
    }

    [Fact]
    public void ARemoteServerHasNoProgram()
    {
        var integration = Sample.Remote() with
        {
            Transport = new IntegrationTransport { Kind = McpTransportKind.StreamableHttp, Endpoint = "https://mcp.example.com/mcp", Command = @"C:\x.exe" },
        };
        Assert.NotEmpty(IntegrationRules.Problems(integration));
    }

    [Fact]
    public void AProgramHasNoAddress()
    {
        var integration = Sample.Program() with
        {
            Transport = new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = @"C:\Tools\server.exe", Endpoint = "https://mcp.example.com/mcp" },
        };
        Assert.NotEmpty(IntegrationRules.Problems(integration));
    }

    [Theory]
    [InlineData(@"C:\Tools\server.exe", true)]
    [InlineData(@"D:\a b\c d\server.EXE", true)]
    [InlineData(@"C:/Tools/server.exe", true)]
    [InlineData(@"server.exe", false)]
    [InlineData(@"..\server.exe", false)]
    [InlineData(@"\\host\share\server.exe", false)]
    [InlineData(@"\\?\C:\Tools\server.exe", false)]
    [InlineData(@"C:\Tools\server.bat", false)]
    [InlineData(@"C:\Tools\server.cmd", false)]
    [InlineData(@"C:\Tools\server.ps1", false)]
    [InlineData(@"C:\Tools\server.js", false)]
    [InlineData(@"C:\Tools\server", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", false)]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", false)]
    [InlineData(@"C:\Windows\System32\wsl.exe", false)]
    [InlineData(@"C:\Windows\System32\wscript.exe", false)]
    [InlineData(@"C:\Windows\System32\mshta.exe", false)]
    [InlineData(@"C:\Windows\System32\rundll32.exe", false)]
    [InlineData(@"C:\Windows\System32\regsvr32.exe", false)]
    [InlineData("", false)]
    public void AProgramIsAnExeByItsFullPathAndNotAShell(string command, bool valid)
    {
        Assert.Equal(valid, McpLaunchRules.Problem(command, [], null, new Dictionary<string, string>()) is null);
    }

    [Fact]
    public void ArgumentsAreLimitedAndMayNotHoldControlCharacters()
    {
        Assert.Null(McpLaunchRules.Problem(@"C:\Tools\server.exe", ["--flag", "value with spaces", "a\tb"], null, null));
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", ["bad\nvalue"], null, null));
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", ["bad\0value"], null, null));
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", [.. Enumerable.Repeat("x", McpLaunchRules.MaxArguments + 1)], null, null));
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", [new string('x', McpLaunchRules.MaxValueLength + 1)], null, null));
    }

    [Fact]
    public void TheWorkingFolderIsAFullLocalPath()
    {
        Assert.Null(McpLaunchRules.Problem(@"C:\Tools\server.exe", [], @"C:\Tools", null));
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", [], "Tools", null));
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", [], @"\\host\share", null));
    }

    [Theory]
    [InlineData("API_KEY", true)]
    [InlineData("_x1", true)]
    [InlineData("1X", false)]
    [InlineData("A-B", false)]
    [InlineData("A=B", false)]
    [InlineData("", false)]
    public void EnvironmentVariablesHaveNamesOfLettersDigitsAndUnderscores(string name, bool valid)
    {
        Assert.Equal(valid, McpLaunchRules.Problem(@"C:\Tools\server.exe", [], null, new Dictionary<string, string> { [name] = "v" }) is null);
    }

    [Fact]
    public void EnvironmentValuesMayNotHoldControlCharacters()
    {
        Assert.NotNull(McpLaunchRules.Problem(@"C:\Tools\server.exe", [], null, new Dictionary<string, string> { ["A"] = "x\r\ny" }));
    }

    [Fact]
    public void ASecretIsNamedWithTheSecretStoresRules()
    {
        var integration = Sample.Remote() with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.BearerToken,
                Secrets = [new IntegrationSecretBinding("Authorization", "Not A Valid Name")],
            },
        };
        Assert.NotEmpty(IntegrationRules.Problems(integration));
        Assert.Empty(IntegrationRules.Problems(Sample.Remote() with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.BearerToken,
                Secrets = [new IntegrationSecretBinding("Authorization", "mcp.todoist.token")],
            },
        }));
    }

    [Fact]
    public void ABearerTokenIsOneSecretForAServerOverHttp()
    {
        var token = new IntegrationSecretBinding("Authorization", "mcp.token");
        Assert.Empty(IntegrationRules.Problems(Sample.Remote() with { Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken, Secrets = [token] } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken, Secrets = [token, token] } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Program() with { Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken, Secrets = [token] } }));
    }

    [Theory]
    [InlineData("X-Api-Key", true)]
    [InlineData("api_key", true)]
    [InlineData("Authorization", true)]
    [InlineData("Bad Header", false)]
    [InlineData("Bad:Header", false)]
    [InlineData("Host", false)]
    [InlineData("Content-Type", false)]
    [InlineData("Mcp-Session-Id", false)]
    [InlineData("MCP-Protocol-Version", false)]
    [InlineData("Mcp-Method", false)]
    [InlineData("Transfer-Encoding", false)]
    public void AKeyInAHeaderNamesARealHeaderThatTheTransportDoesNotSetItself(string header, bool valid)
    {
        var authentication = new IntegrationAuthentication
        {
            Kind = IntegrationAuthKind.HeaderKey,
            Secrets = [new IntegrationSecretBinding(header, "mcp.key")],
        };
        Assert.Equal(valid, IntegrationRules.Problems(Sample.Remote() with { Authentication = authentication }).Count == 0);
    }

    [Fact]
    public void ASecretGivenAsAnEnvironmentVariableIsForAProgramOnly()
    {
        var authentication = new IntegrationAuthentication
        {
            Kind = IntegrationAuthKind.EnvironmentSecret,
            Secrets = [new IntegrationSecretBinding("API_KEY", "mcp.key")],
        };
        Assert.Empty(IntegrationRules.Problems(Sample.Program() with { Authentication = authentication }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Authentication = authentication }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Program() with
        {
            Authentication = authentication with { Secrets = [new IntegrationSecretBinding("not valid", "mcp.key")] },
        }));
    }

    [Fact]
    public void NoSecretsGoWithNoSignIn()
    {
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with
        {
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.None, Secrets = [new IntegrationSecretBinding("Authorization", "mcp.token")] },
        }));
    }

    [Fact]
    public void ADestructivePermissionCannotBeRequiredOfATool()
    {
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Permissions = new IntegrationPermissions { RequiredCapability = PermissionCapability.DestructiveActions } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Permissions = new IntegrationPermissions { RequiredCapability = (PermissionCapability)99 } }));
        Assert.Empty(IntegrationRules.Problems(Sample.Remote() with { Permissions = new IntegrationPermissions { RequiredCapability = PermissionCapability.Calendar } }));
    }

    [Fact]
    public void ToolNameListsHoldOnlyNamesAServerMayUse()
    {
        Assert.Empty(IntegrationRules.Problems(Sample.Remote() with { Permissions = new IntegrationPermissions { ReadOnlyTools = ["get_user", "admin.tools.list", "DATA-export_v2"] } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Permissions = new IntegrationPermissions { ReadOnlyTools = ["has space"] } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Permissions = new IntegrationPermissions { BlockedTools = [new string('a', IntegrationRules.MaxToolNameLength + 1)] } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Capabilities = new IntegrationCapabilities { ToolNames = ["bad name"] } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with
        {
            Capabilities = new IntegrationCapabilities { ToolNames = [.. Enumerable.Range(0, IntegrationRules.MaxToolNames + 1).Select(number => "tool" + number)] },
        }));
    }

    [Theory]
    [InlineData("2026-07-28", true)]
    [InlineData("2024-11-05", true)]
    [InlineData("latest", false)]
    [InlineData("2026-7-28", false)]
    public void AProtocolVersionIsADate(string version, bool valid)
    {
        Assert.Equal(valid, IntegrationRules.Problems(Sample.Remote() with { Capabilities = new IntegrationCapabilities { ProtocolVersion = version } }).Count == 0);
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.0.0-beta.1+build5", true)]
    [InlineData("v2", true)]
    [InlineData("1 2", false)]
    [InlineData("", false)]
    public void AnInstalledVersionIsAVersionString(string version, bool valid)
    {
        Assert.Equal(valid, IntegrationRules.Problems(Sample.Remote() with { InstalledVersion = version }).Count == 0);
    }

    [Fact]
    public void AnUndefinedEnumIsRefused()
    {
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Source = new IntegrationSource((IntegrationSourceKind)99, null) }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Health = new IntegrationHealth { Status = (IntegrationHealthStatus)99 } }));
        Assert.NotEmpty(IntegrationRules.Problems(Sample.Remote() with { Transport = new IntegrationTransport { Kind = (McpTransportKind)99, Endpoint = "https://mcp.example.com/mcp" } }));
    }

    [Fact]
    public void ProblemsNameTheRuleAndNeverRepeatAValue()
    {
        var secret = "super-secret-value-123";
        var integration = Sample.Remote(endpoint: "http://evil.example.com/" + secret) with
        {
            Name = "Name\n" + secret,
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken, Secrets = [new IntegrationSecretBinding("Authorization", "Bad Name " + secret)] },
        };
        var problems = IntegrationRules.Problems(integration);
        Assert.NotEmpty(problems);
        Assert.DoesNotContain(problems, problem => problem.Contains(secret, StringComparison.Ordinal) || problem.Contains("evil.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void ANullIntegrationIsAProblemNotAnException()
    {
        Assert.NotEmpty(IntegrationRules.Problems(null));
    }
}
