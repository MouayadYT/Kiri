using System.Text.Json.Serialization;

namespace Assistant.Core.Settings;

/// <summary>How long conversation history is kept. Each value other than <see cref="UntilDeleted"/> is a number of days.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HistoryRetention>))]
public enum HistoryRetention
{
    /// <summary>Kept until the user deletes it.</summary>
    UntilDeleted = 0,

    /// <summary>Deleted after 7 days.</summary>
    SevenDays = 7,

    /// <summary>Deleted after 30 days.</summary>
    ThirtyDays = 30,

    /// <summary>Deleted after 90 days.</summary>
    NinetyDays = 90,
}
