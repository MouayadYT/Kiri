using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>The kind of user-scoped context a <see cref="ContextItem"/> holds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextItemType>))]
public enum ContextItemType
{
    /// <summary>Text selected in an application.</summary>
    Selection = 0,

    /// <summary>A file the user attached.</summary>
    File = 1,

    /// <summary>A captured screen region.</summary>
    Screenshot = 2,

    /// <summary>Readable text of a browser page.</summary>
    Page = 3,

    /// <summary>
    /// The Files scope: ground the answer in files that Windows Search retrieves when the request runs.
    /// </summary>
    SearchResults = 4,

    /// <summary>
    /// A picture the user attached, such as a photo or an image file. A model that reads images gets its pixels; one
    /// that cannot gets its recognized text, if it has any.
    /// </summary>
    Image = 5,

    /// <summary>
    /// Notes the local model took on a part of a file the user attached, for the question asked about it, when the files were too
    /// long to read at once (PROJECT_SPEC §5.5, several files): what the answer is put together from in place of the file's text.
    /// </summary>
    FileNotes = 6,
}
