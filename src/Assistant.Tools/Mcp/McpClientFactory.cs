using Assistant.Core.Contracts;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp.Auth;

namespace Assistant.Tools.Mcp;

/// <summary>
/// Makes the <see cref="IMcpClient"/> for an installed integration: it checks the record against the rules once more (<see cref="IntegrationRules"/>:
/// a record that breaks one is never connected to, however it got in), chooses the transport, and, as the client connects, reads the secrets the
/// integration names from the secret store and puts them where they go (a bearer token or key in a header, a secret in the program's environment).
/// A secret is read for the connection and kept only for as long as it lasts; it is never in the record, a log, an exception or a message.
/// </summary>
public sealed class McpClientFactory : IMcpClientFactory
{
    private readonly ISecretStore? _secrets;
    private readonly IMcpAccessTokens? _tokens;
    private readonly McpClientOptions _options;
    private readonly Func<HttpMessageHandler> _handlers;

    /// <summary>Creates the factory.</summary>
    /// <param name="secrets">Where the secrets are kept; without it an integration that names one cannot be connected to.</param>
    /// <param name="options">How long and how much a client waits for and accepts; the defaults when not given.</param>
    /// <param name="tokens">Gives the access token of an app that is signed in with OAuth, renewed when it has run out; without it such an app is sent the secret it names, as a bearer token.</param>
    public McpClientFactory(ISecretStore? secrets = null, McpClientOptions? options = null, IMcpAccessTokens? tokens = null)
        : this(secrets, options, McpHttp.CreateHandler, tokens)
    {
    }

    internal McpClientFactory(ISecretStore? secrets, McpClientOptions? options, Func<HttpMessageHandler> handlers, IMcpAccessTokens? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _tokens = tokens;
        _secrets = secrets;
        _options = options ?? new McpClientOptions();
        _handlers = handlers;
    }

    /// <inheritdoc/>
    public IMcpClient Create(InstalledIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        if (IntegrationRules.Problems(integration).Count > 0)
        {
            throw new McpException(McpFailure.NotConfigured);
        }

        var options = _options with { KnownProtocolVersion = integration.Capabilities.ProtocolVersion };
        return new McpClient(
            integration.Transport.Kind, (kind, cancellationToken) => CreateTransportAsync(integration, kind, options, cancellationToken), options);
    }

    private async ValueTask<IMcpTransport> CreateTransportAsync(
        InstalledIntegration integration, McpTransportKind kind, McpClientOptions options, CancellationToken cancellationToken)
    {
        var transport = integration.Transport;
        var authentication = integration.Authentication;
        if (kind == McpTransportKind.Stdio)
        {
            if (transport.Kind != McpTransportKind.Stdio)
            {
                throw new McpException(McpFailure.NotConfigured);
            }

            var environment = new Dictionary<string, string>(transport.Environment, StringComparer.OrdinalIgnoreCase);
            if (authentication.Kind == IntegrationAuthKind.EnvironmentSecret)
            {
                foreach (var binding in authentication.Secrets)
                {
                    environment[binding.Target] = await ReadSecretAsync(binding, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (authentication is { Kind: IntegrationAuthKind.OAuth, TokenVariable: { } variable } && _tokens is not null)
            {
                // A program signed in with OAuth is given a current access token each time it starts, and nothing else of the sign-in.
                environment[variable] = await _tokens.GetAccessTokenAsync(integration, cancellationToken).ConfigureAwait(false);
            }

            return new StdioMcpTransport(new McpLaunch(transport.Command!, transport.Arguments, transport.WorkingDirectory, environment), options);
        }

        if (transport.Kind == McpTransportKind.Stdio || McpEndpointRules.Problem(transport.Endpoint, out var endpoint) is not null || endpoint is null)
        {
            throw new McpException(McpFailure.NotConfigured);
        }

        var headers = new Dictionary<string, string>(transport.Headers, StringComparer.OrdinalIgnoreCase);
        switch (authentication.Kind)
        {
            case IntegrationAuthKind.OAuth when _tokens is not null:
                headers["Authorization"] = "Bearer " + await _tokens.GetAccessTokenAsync(integration, cancellationToken).ConfigureAwait(false);
                break;
            case IntegrationAuthKind.BearerToken or IntegrationAuthKind.OAuth when authentication.Secrets.Count > 0:
                headers["Authorization"] = "Bearer " + await ReadSecretAsync(authentication.Secrets[0], cancellationToken).ConfigureAwait(false);
                break;
            case IntegrationAuthKind.HeaderKey:
                foreach (var binding in authentication.Secrets)
                {
                    headers[binding.Target] = await ReadSecretAsync(binding, cancellationToken).ConfigureAwait(false);
                }

                break;
        }

        return kind == McpTransportKind.LegacySse
            ? new LegacySseMcpTransport(endpoint, headers, _handlers(), options)
            : new StreamableHttpMcpTransport(endpoint, headers, _handlers(), options);
    }

    // A secret, or the failure that says the integration cannot be signed in: it is not there, the store could not be read, or it could not be sent as it is.
    private async Task<string> ReadSecretAsync(IntegrationSecretBinding binding, CancellationToken cancellationToken)
    {
        if (_secrets is null)
        {
            throw new McpException(McpFailure.AuthRequired);
        }

        string? secret;
        try
        {
            secret = await _secrets.GetAsync(binding.SecretName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SecretStoreException or ArgumentException)
        {
            throw new McpException(McpFailure.AuthRequired, inner: exception);
        }

        // A value that could not be sent as a header or an environment variable as it is (it holds a line break) is not sent changed.
        if (string.IsNullOrEmpty(secret))
        {
            throw new McpException(McpFailure.AuthRequired);
        }

        if (secret.Any(character => char.IsControl(character) && character != '\t'))
        {
            throw new McpException(McpFailure.NotConfigured);
        }

        return secret;
    }
}
