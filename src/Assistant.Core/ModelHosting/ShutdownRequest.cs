namespace Assistant.Core.ModelHosting;

/// <summary>
/// Asks the host to exit. The host stops the requests still running and answers with
/// <see cref="ShutdownAccepted"/>, then closes the connection and exits.
/// </summary>
public sealed record ShutdownRequest : ModelHostRequest;
