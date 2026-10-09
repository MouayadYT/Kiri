namespace Assistant.Core.ModelHosting;

/// <summary>Reports how far a <see cref="LoadModelRequest"/> has come, so load progress can be shown.</summary>
/// <param name="ModelId">The model being loaded.</param>
/// <param name="Fraction">The part loaded so far, from 0 to 1.</param>
public sealed record ModelLoadProgress(string ModelId, double Fraction) : ModelHostReply
{
    internal override bool IsWellFormed() => !string.IsNullOrWhiteSpace(ModelId) && Fraction is >= 0 and <= 1;
}
