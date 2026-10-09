namespace Assistant.Core.ModelHosting;

/// <summary>
/// Answers a <see cref="ShutdownRequest"/>: the host has stopped its work, and closes the connection and exits next.
/// </summary>
public sealed record ShutdownAccepted : ModelHostReply;
