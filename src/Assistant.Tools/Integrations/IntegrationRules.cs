using System.Text.RegularExpressions;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>
/// What an <see cref="InstalledIntegration"/> may hold (PROJECT_SPEC §4.8, step 104), checked when one is stored, again when the file is read,
/// and again before it is connected to. A record that breaks a rule is refused (or, from the file, left out), never repaired: a launch command
/// or an address that is not accepted must not be run because it was "fixed up". The problems it reports name the field and never repeat a
/// value, so they are safe to show or to log.
/// </summary>
public static partial class IntegrationRules
{
    /// <summary>The longest id, in characters.</summary>
    public const int MaxIdLength = 40;

    /// <summary>The longest name, in characters.</summary>
    public const int MaxNameLength = 80;

    /// <summary>The longest origin, version or scope text, in characters.</summary>
    public const int MaxOriginLength = 300;

    /// <summary>The most tool names kept for an integration, and the same limit on the lists of tool names in its permissions.</summary>
    public const int MaxToolNames = 500;

    /// <summary>The longest tool name an MCP server may use, in characters (the protocol's own limit).</summary>
    public const int MaxToolNameLength = 128;

    private const int MaxVersionLength = 64;
    private const int MaxSecrets = 16;
    private const int MaxScopes = 64;

    // Letters and digits only: the id is one word of a tool's name (mcp_<id>_<tool>), which the registry's rule about names that read as a
    // command (ToolDefinitionGuard) looks at word by word.
    [GeneratedRegex("^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z._+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolVersionPattern();

    // The characters an MCP tool name may have (the protocol says: letters, digits, underscore, hyphen and dot).
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    // An HTTP header name: the characters of a token.
    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNamePattern();

    /// <summary>Whether <paramref name="id"/> can be an integration's id: lower-case letters and digits, starting with a letter, of at most 40 characters.</summary>
    public static bool IsValidId(string? id) => id is not null && id.Length <= MaxIdLength && IdPattern().IsMatch(id);

    /// <summary>Whether <paramref name="version"/> can be the version of an installed integration: at most 64 characters of letters, digits and <c>. _ + -</c>, starting with a letter or digit.</summary>
    public static bool IsValidVersion(string? version) => version is not null && version.Length <= MaxVersionLength && VersionPattern().IsMatch(version);

    /// <summary>Whether <paramref name="name"/> can be the name of a header that carries a key to a server reached over HTTP: a token, and not one of the headers the transport sets itself.</summary>
    public static bool IsValidHeaderKeyName(string? name) => name is not null && HeaderNamePattern().IsMatch(name) && !IsReservedHeader(name);

    /// <summary>Whether <paramref name="name"/> can be the name of a tool of an MCP server.</summary>
    public static bool IsValidToolName(string? name) => name is not null && ToolNamePattern().IsMatch(name);

    /// <summary>Everything wrong with <paramref name="integration"/>; empty when it breaks no rule.</summary>
    public static IReadOnlyList<string> Problems(InstalledIntegration? integration)
    {
        if (integration is null)
        {
            return ["The integration is missing."];
        }

        var problems = new List<string>();
        if (!IsValidId(integration.Id))
        {
            problems.Add("The id must be lower-case letters and digits, starting with a letter, of at most 40 characters.");
        }

        if (string.IsNullOrWhiteSpace(integration.Name) || integration.Name.Length > MaxNameLength || integration.Name.Any(char.IsControl))
        {
            problems.Add("The name is missing, too long, or has control characters.");
        }

        if (integration.Source is null || !Enum.IsDefined(integration.Source.Kind)
            || integration.Source.Origin is { } origin && (origin.Length > MaxOriginLength || origin.Any(char.IsControl)))
        {
            problems.Add("The source is not valid.");
        }

        if (integration.InstalledVersion is { } version && (version.Length > MaxVersionLength || !VersionPattern().IsMatch(version)))
        {
            problems.Add("The version is not valid.");
        }

        AddTransportProblems(integration.Transport, problems);
        AddCapabilityProblems(integration.Capabilities, problems);
        AddAuthenticationProblems(integration.Authentication, integration.Transport, problems);
        AddPermissionProblems(integration.Permissions, problems);
        if (integration.Health is null || !Enum.IsDefined(integration.Health.Status)
            || integration.Health.Failure is { } failure && !Enum.IsDefined(failure) || integration.Health.ConsecutiveFailures < 0)
        {
            problems.Add("The health is not valid.");
        }

        AddManagedProblems(integration.Managed, problems);
        return problems;
    }

    private static void AddManagedProblems(ManagedInstall? managed, List<string> problems)
    {
        if (managed is null)
        {
            return;
        }

        if (!Enum.IsDefined(managed.Kind) || !Enum.IsDefined(managed.Trust) || managed.Runtime is { } runtime && !Enum.IsDefined(runtime)
            || string.IsNullOrWhiteSpace(managed.Package) || managed.Package.Length > MaxOriginLength || managed.Package.Any(char.IsControl)
            || managed.Hash is { } hash && (hash.Length > 140 || hash.Any(char.IsControl))
            || managed.Commit is { } commit && (commit.Length > 64 || commit.Any(char.IsControl))
            || managed.Repository is { } repository && (repository.Length > MaxOriginLength || repository.Any(char.IsControl))
            || managed.Publisher is { } publisher && (publisher.Length > MaxNameLength || publisher.Any(char.IsControl))
            || managed.RuntimeVersion is { } runtimeVersion && !IsValidVersion(runtimeVersion)
            || managed.Fingerprint is { } fingerprint && (fingerprint.Length > 128 || fingerprint.Any(char.IsControl)))
        {
            problems.Add("The record of how it was installed is not valid.");
        }
    }

    private static void AddTransportProblems(IntegrationTransport? transport, List<string> problems)
    {
        if (transport is null || !Enum.IsDefined(transport.Kind))
        {
            problems.Add("The transport is missing or unknown.");
            return;
        }

        if (transport.Kind == McpTransportKind.Stdio)
        {
            if (transport.Headers is null || transport.Headers.Count > 0) problems.Add("A local program has no HTTP headers.");
            if (transport.Endpoint is not null)
            {
                problems.Add("A program started by the Assistant has no address.");
            }

            if (McpLaunchRules.Problem(transport.Command, transport.Arguments, transport.WorkingDirectory, transport.Environment) is { } launch)
            {
                problems.Add(launch);
            }

            return;
        }

        if (transport.Headers is null || transport.Headers.Count > 1 || transport.Headers.Any(header =>
            !string.Equals(header.Key, "X-Tavily-Access-Mode", StringComparison.OrdinalIgnoreCase) || header.Value != "keyless"))
            problems.Add("Only the public Tavily keyless access-mode header is supported. Store keys in Authentication.");

        if (transport.Command is not null || transport.Arguments is { Count: > 0 } || transport.WorkingDirectory is not null
            || transport.Environment is { Count: > 0 })
        {
            problems.Add("A server reached over HTTP has no program to start.");
        }

        if (McpEndpointRules.Problem(transport.Endpoint) is { } endpoint)
        {
            problems.Add(endpoint);
        }
    }

    private static void AddCapabilityProblems(IntegrationCapabilities? capabilities, List<string> problems)
    {
        if (capabilities is null
            || capabilities.ProtocolVersion is { } protocol && !ProtocolVersionPattern().IsMatch(protocol)
            || capabilities.ToolNames is null || capabilities.ToolNames.Count > MaxToolNames
            || capabilities.ToolNames.Any(name => !IsValidToolName(name)))
        {
            problems.Add("The capabilities are not valid.");
        }
    }

    private static void AddAuthenticationProblems(IntegrationAuthentication? authentication, IntegrationTransport? transport, List<string> problems)
    {
        if (authentication is null || !Enum.IsDefined(authentication.Kind) || !Enum.IsDefined(authentication.State)
            || authentication.Secrets is null || authentication.Secrets.Count > MaxSecrets
            || authentication.GrantedScopes is null || authentication.GrantedScopes.Count > MaxScopes
            || authentication.GrantedScopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Length > MaxOriginLength || scope.Any(char.IsControl)))
        {
            problems.Add("The sign-in is not valid.");
            return;
        }

        if (authentication.Secrets.Any(binding => binding is null || !SecretNames.IsValid(binding.SecretName)))
        {
            problems.Add("A secret is named in a way the secret store does not accept.");
            return;
        }

        var stdio = transport?.Kind == McpTransportKind.Stdio;
        switch (authentication.Kind)
        {
            case IntegrationAuthKind.None or IntegrationAuthKind.OAuth:
                if (authentication.Secrets.Count > 0 && authentication.Kind == IntegrationAuthKind.None)
                {
                    problems.Add("A sign-in of no kind has no secrets.");
                }

                if (authentication.TokenVariable is { } variable && (!stdio || authentication.Kind != IntegrationAuthKind.OAuth || !McpLaunchRules.IsValidVariableName(variable)))
                {
                    problems.Add("The token is given to a program by an environment variable that the program can have.");
                }

                break;
            case IntegrationAuthKind.BearerToken:
                if (stdio || authentication.Secrets.Count != 1)
                {
                    problems.Add("A bearer token is one secret, for a server reached over HTTP.");
                }

                break;
            case IntegrationAuthKind.HeaderKey:
                if (stdio || authentication.Secrets.Count == 0 || authentication.Secrets.Any(binding => !HeaderNamePattern().IsMatch(binding.Target ?? string.Empty)
                    || IsReservedHeader(binding.Target!)))
                {
                    problems.Add("A key sent in a header names the header, for a server reached over HTTP.");
                }

                break;
            case IntegrationAuthKind.EnvironmentSecret:
                if (!stdio || authentication.Secrets.Count == 0 || authentication.Secrets.Any(binding => !McpLaunchRules.IsValidVariableName(binding.Target)))
                {
                    problems.Add("A secret given as an environment variable names the variable, for a program started by the Assistant.");
                }

                break;
        }
    }

    private static void AddPermissionProblems(IntegrationPermissions? permissions, List<string> problems)
    {
        if (permissions is null
            || permissions.RequiredCapability is { } capability && (!Enum.IsDefined(capability) || capability == PermissionCapability.DestructiveActions)
            || permissions.ReadOnlyTools is null || permissions.BlockedTools is null
            || permissions.ReadOnlyTools.Count > MaxToolNames || permissions.BlockedTools.Count > MaxToolNames
            || permissions.ReadOnlyTools.Any(name => !IsValidToolName(name)) || permissions.BlockedTools.Any(name => !IsValidToolName(name)))
        {
            problems.Add("The permissions are not valid.");
        }
    }

    // Headers the transport sets itself, which a key may not replace.
    private static bool IsReservedHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MCP-Protocol-Version", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase);
}
