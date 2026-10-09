namespace Assistant.Core.ModelHosting;

/// <summary>
/// Asks the host whether it is up and what it holds, answered by a <see cref="HealthReport"/>. It is also the ping:
/// the app sends it first on every connection, to check that both sides speak the same protocol version.
/// </summary>
public sealed record HealthRequest : ModelHostRequest;
