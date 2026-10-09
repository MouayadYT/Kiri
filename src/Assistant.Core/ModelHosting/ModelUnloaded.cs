namespace Assistant.Core.ModelHosting;

/// <summary>Answers an <see cref="UnloadModelRequest"/>: no model is loaded now.</summary>
public sealed record ModelUnloaded : ModelHostReply
{
    /// <summary>The model that was unloaded, or <see langword="null"/> when none was loaded.</summary>
    public string? ModelId { get; init; }
}
