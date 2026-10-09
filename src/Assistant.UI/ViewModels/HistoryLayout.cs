namespace Assistant.UI.ViewModels;

/// <summary>How the History window's sidebar shows the conversations (PROJECT_SPEC §4.3).</summary>
public enum HistoryLayout
{
    /// <summary>Cards in two staggered columns, each with when it changed, its title and a preview or its image.</summary>
    Grid,

    /// <summary>Compact rows of title and preview, or image, grouped under headers by how recently they changed.</summary>
    List,
}
