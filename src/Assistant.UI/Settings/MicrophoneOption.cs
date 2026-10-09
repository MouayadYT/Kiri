namespace Assistant.UI.Settings;

/// <summary>One choice in the list of microphones: the one Windows uses, or one that is plugged in (or was chosen and is not).</summary>
/// <param name="Id">Windows id for the device, or <see langword="null"/> for the one Windows uses.</param>
/// <param name="Name">What the list shows.</param>
/// <param name="IsMissing">Whether it is a choice made earlier of a device that is not connected now.</param>
public sealed record MicrophoneOption(string? Id, string Name, bool IsMissing)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}
