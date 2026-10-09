using System.Text.Json.Nodes;
using Assistant.Core.Contracts;

namespace Assistant.Data.Settings;

internal static class DefaultSettingsMigrations
{
    public static IReadOnlyList<SettingsMigration> All { get; } = [new(1, document =>
    {
        // Only replace the old shipped defaults; retain custom limits and custom model windows.
        if (document["contextLimits"] is not JsonObject limits || limits["normalContextTokens"]?.ToString() != "32768" || limits["heavyContextTokens"]?.ToString() != "32768") return;
        if (document["model"] is JsonObject model && model["contextLength"] is { } window && window.ToString() != "8192") return;
        limits["normalContextTokens"] = 8192; limits["heavyContextTokens"] = 8192;
        if (limits["reservedOutputTokens"]?.ToString() == "4096") limits["reservedOutputTokens"] = 1024;
    }), new(2, document =>
    {
        // Migrate the previous 8K defaults once, without changing an explicitly customized window or limit.
        if (document["contextLimits"] is not JsonObject limits || limits["normalContextTokens"]?.ToString() != "8192" || limits["heavyContextTokens"]?.ToString() != "8192") return;
        var model = document["model"] as JsonObject;
        if (model?["contextLength"] is { } window && window.ToString() != "8192") return;
        limits["normalContextTokens"] = 4096;
        limits["heavyContextTokens"] = 4096;
        if (model?["contextLength"] is not null) model["contextLength"] = 4096;
    }), new(3, document =>
    {
        // The 4K defaults become 8,000 tokens for ordinary conversations and 32,000 while a conversation carries files, and the window that was written
        // down with them is let go, so that the model is loaded with whichever of the two the conversation needs. Limits or a window the user set
        // themselves are kept as they are.
        if (document["contextLimits"] is not JsonObject limits || limits["normalContextTokens"]?.ToString() != "4096" || limits["heavyContextTokens"]?.ToString() != "4096") return;
        var model = document["model"] as JsonObject;
        if (model?["contextLength"] is { } window && window.ToString() != "4096") return;
        limits["normalContextTokens"] = ModelFiles.DefaultContextLength;
        limits["heavyContextTokens"] = ModelFiles.DocumentContextLength;
        model?.Remove("contextLength");
    })];
}
