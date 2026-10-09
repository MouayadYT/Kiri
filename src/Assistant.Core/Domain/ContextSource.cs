using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>
/// How a <see cref="ContextItem"/> came to be in the conversation's context, which decides how it ranks when the context
/// does not all fit the model's window (PROJECT_SPEC §5.5).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextSource>))]
public enum ContextSource
{
    /// <summary>
    /// Not said: the item ranks as the kind of context it is (<see cref="ContextItemType"/>), so a file or a picture the
    /// user attached is <see cref="UserSelected"/>, text, a screenshot or a page from the screen is
    /// <see cref="CurrentScreen"/>, and the Files scope is <see cref="Retrieval"/>.
    /// </summary>
    Unspecified = 0,

    /// <summary>The user picked it on purpose: a file or a picture they attached, or files they selected in File Explorer.</summary>
    UserSelected = 1,

    /// <summary>It was captured from what is on the screen now: the selected text, a screen region, the page in the browser.</summary>
    CurrentScreen = 2,

    /// <summary>The Assistant found it for the question, by searching the user's files.</summary>
    Retrieval = 3,

    /// <summary>A tool the model or the Assistant ran returned it.</summary>
    Tool = 4,
}
