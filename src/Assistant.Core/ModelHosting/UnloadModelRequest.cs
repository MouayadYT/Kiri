namespace Assistant.Core.ModelHosting;

/// <summary>
/// Unloads the loaded model and frees its memory, for example after the idle timeout. Answered by
/// <see cref="ModelUnloaded"/>, also when no model was loaded.
/// </summary>
public sealed record UnloadModelRequest : ModelHostRequest;
