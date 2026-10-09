using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>The changes to an installed integration that are made often, each as one <see cref="IInstalledIntegrationRegistry.UpdateAsync"/>.</summary>
public static class InstalledIntegrationRegistryExtensions
{
    /// <summary>How long a record that nothing important has changed in is left as it is, rather than written again.</summary>
    private static readonly TimeSpan HealthRewriteInterval = TimeSpan.FromHours(1);

    /// <summary>Turns an integration on or off.</summary>
    public static Task<InstalledIntegration> SetEnabledAsync(
        this IInstalledIntegrationRegistry registry, string id, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.UpdateAsync(id, integration => integration with { Enabled = enabled }, cancellationToken);
    }

    /// <summary>
    /// Records how the last attempt to reach an integration went. A result that says the same as the one recorded (the same state and failure)
    /// is not written again until an hour has passed, so that a connection made for every request does not rewrite the file for each.
    /// </summary>
    public static Task<InstalledIntegration> RecordHealthAsync(
        this IInstalledIntegrationRegistry registry,
        string id,
        IntegrationHealthStatus status,
        McpFailure? failure,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.UpdateAsync(
            id,
            integration =>
            {
                var health = integration.Health;
                var failures = status == IntegrationHealthStatus.Healthy ? 0 : health.ConsecutiveFailures + 1;
                if (health.Status == status && health.Failure == failure && health.CheckedAt is { } checkedAt && now - checkedAt < HealthRewriteInterval)
                {
                    return integration;
                }

                return integration with
                {
                    Health = new IntegrationHealth { Status = status, Failure = failure, CheckedAt = now, ConsecutiveFailures = failures },
                };
            },
            cancellationToken);
    }

    /// <summary>
    /// Records what a server said it can do when it was connected to: the protocol version agreed, the features, and the names of its tools, so
    /// that a request can be matched to the app without starting it.
    /// </summary>
    public static Task<InstalledIntegration> RecordCapabilitiesAsync(
        this IInstalledIntegrationRegistry registry,
        string id,
        IntegrationCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        return registry.UpdateAsync(
            id,
            integration => IsSameCapabilities(integration.Capabilities, capabilities)
                ? integration
                : integration with { Capabilities = capabilities },
            cancellationToken);
    }

    /// <summary>Records how an integration's sign-in stands.</summary>
    public static Task<InstalledIntegration> RecordAuthenticationStateAsync(
        this IInstalledIntegrationRegistry registry,
        string id,
        IntegrationAuthState state,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.UpdateAsync(
            id,
            integration => integration.Authentication.State == state
                ? integration
                : integration with { Authentication = integration.Authentication with { State = state, CheckedAt = now } },
            cancellationToken);
    }

    /// <summary>Records the transport a server turned out to speak (a server found to speak the older HTTP+SSE transport is reached that way next time).</summary>
    public static Task<InstalledIntegration> RecordTransportKindAsync(
        this IInstalledIntegrationRegistry registry, string id, McpTransportKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.UpdateAsync(
            id,
            integration => integration.Transport.Kind == kind
                ? integration
                : integration with { Transport = integration.Transport with { Kind = kind } },
            cancellationToken);
    }

    // The same, apart from when the names were read, which alone is not worth a write.
    private static bool IsSameCapabilities(IntegrationCapabilities current, IntegrationCapabilities next) =>
        current.Tools == next.Tools && current.Resources == next.Resources && current.Prompts == next.Prompts
        && current.ProtocolVersion == next.ProtocolVersion && current.ToolNames.SequenceEqual(next.ToolNames, StringComparer.Ordinal);
}
