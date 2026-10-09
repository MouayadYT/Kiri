namespace Assistant.Core.ModelHosting;

/// <summary>
/// A message of the model-host protocol (<see cref="ModelHostProtocol"/>). Each concrete type has a name on the wire,
/// given by <see cref="ModelHostSerializer"/>.
/// </summary>
public abstract record ModelHostMessage
{
    /// <summary>
    /// Whether the fields a reader relies on are present and in range. A message that is not well formed is answered
    /// with <see cref="ModelHostErrorCode.MalformedMessage"/> and never handled.
    /// </summary>
    internal virtual bool IsWellFormed() => true;
}
