using System.Text;
using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Loads a model, replacing the one loaded before, or stopping a load in progress. The host reports each step as a
/// <see cref="ModelStatusReport"/> and answers with <see cref="ModelLoaded"/>, or with an error such as
/// <see cref="ModelHostErrorCode.ModelNotFound"/>, <see cref="ModelHostErrorCode.ModelLoadFailed"/> or
/// <see cref="ModelHostErrorCode.Cancelled"/>.
/// </summary>
/// <param name="ModelId">The model's identifier: from its manifest, or <see cref="ModelFiles.DeriveModelId"/>.</param>
public sealed record LoadModelRequest(string ModelId) : ModelHostRequest
{
    /// <summary>
    /// The files to load. Until models have manifests the host has no catalog to look <see cref="ModelId"/> up in, so a
    /// request without files is answered with <see cref="ModelHostErrorCode.ModelNotFound"/>.
    /// </summary>
    public ModelFiles? Files { get; init; }

    internal override bool IsWellFormed() =>
        !string.IsNullOrWhiteSpace(ModelId) && (Files is null || Files.IsWellFormed());

    // Keeps the user's paths (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ModelId = {ModelId}, Files = {Files}");
        return true;
    }
}
