using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>Answers a <see cref="LoadModelRequest"/>: the model is loaded and ready.</summary>
/// <param name="Model">The loaded model's identity and capabilities.</param>
public sealed record ModelLoaded(ModelInfo Model) : ModelHostReply
{
    internal override bool IsWellFormed() =>
        Model is not null && !string.IsNullOrWhiteSpace(Model.Id) && Model.ContextLength > 0;
}
