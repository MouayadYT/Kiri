using System.Text.Json;

namespace Assistant.Core.Ipc;

/// <summary>
/// How a <see cref="BrowserSelection"/> is written as a JSON body, the same on the app pipe and on the native-messaging host's standard
/// streams: <c>{"selectionText":"…","selectionTruncated":false,"pageTitle":"…","pageUrl":"…","browserName":"…"}</c>, with
/// <c>"nearbyBefore"</c> and <c>"nearbyAfter"</c> added when the extension sends page text around the selection (step 88).
/// </summary>
public static class BrowserSelectionJson
{
    /// <summary>Writes <paramref name="selection"/> as the object <paramref name="writer"/> is positioned to take (a property named <c>body</c>).</summary>
    public static void WriteBody(Utf8JsonWriter writer, BrowserSelection selection)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(selection);
        writer.WriteStartObject("body");
        writer.WriteString("selectionText", selection.Text);
        writer.WriteBoolean("selectionTruncated", selection.IsTruncated);
        writer.WriteString("pageTitle", selection.PageTitle);
        writer.WriteString("pageUrl", selection.PageUrl);
        writer.WriteString("browserName", selection.BrowserName);
        if (selection.NearbyBefore.Length > 0)
        {
            writer.WriteString("nearbyBefore", selection.NearbyBefore);
        }

        if (selection.NearbyAfter.Length > 0)
        {
            writer.WriteString("nearbyAfter", selection.NearbyAfter);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads the <c>body</c> of the envelope <paramref name="root"/>. The selected text is required; the flag, the title, the address
    /// and the browser's name are optional (the browser may not give a title), and so is the page text around the selection. Fields it does not know are ignored.
    /// </summary>
    /// <returns><see langword="true"/> with the selection, or <see langword="false"/> when the body is missing, has a field of the wrong kind, or passes a limit.</returns>
    public static bool TryReadBody(JsonElement root, out BrowserSelection? selection)
    {
        selection = null;
        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("selectionText", out var text) || text.ValueKind != JsonValueKind.String
            || !TryReadOptionalString(body, "pageTitle", out var title)
            || !TryReadOptionalString(body, "pageUrl", out var url)
            || !TryReadOptionalString(body, "browserName", out var browser)
            || !TryReadOptionalString(body, "nearbyBefore", out var nearbyBefore)
            || !TryReadOptionalString(body, "nearbyAfter", out var nearbyAfter))
        {
            return false;
        }

        var truncated = false;
        if (body.TryGetProperty("selectionTruncated", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            truncated = flag.GetBoolean();
        }

        // GetString throws InvalidOperationException for an unpaired surrogate written as an escape: the callers treat that as malformed.
        var read = new BrowserSelection(text.GetString() ?? "", truncated, title, url, browser, nearbyBefore, nearbyAfter);
        if (!read.IsValid)
        {
            return false;
        }

        selection = read;
        return true;
    }

    private static bool TryReadOptionalString(JsonElement body, string name, out string value)
    {
        value = "";
        if (!body.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return true;
    }
}
