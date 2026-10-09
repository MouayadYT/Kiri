namespace Assistant.Core.Settings;

/// <summary>The hosted search engine explicitly enabled by the user.</summary>
public sealed record WebSearchSettings
{
    public bool Enabled { get; init; }
    public WebSearchProvider Provider { get; init; } = WebSearchProvider.Exa;
}

public enum WebSearchProvider { Exa, Tavily, DuckDuckGo }
