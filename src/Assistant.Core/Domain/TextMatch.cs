namespace Assistant.Core.Domain;

/// <summary>Where a search matched in a piece of text: <paramref name="Length"/> characters from <paramref name="Start"/>.</summary>
public readonly record struct TextMatch(int Start, int Length)
{
    /// <summary>The index just after the match.</summary>
    public int End => Start + Length;
}
