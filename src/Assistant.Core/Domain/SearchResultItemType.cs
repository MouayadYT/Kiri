using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>The kind of local item a <see cref="SearchResultItem"/> refers to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SearchResultItemType>))]
public enum SearchResultItemType
{
    /// <summary>An installed app, including packaged apps.</summary>
    App = 0,

    /// <summary>A file.</summary>
    File = 1,

    /// <summary>A folder.</summary>
    Folder = 2,
}
