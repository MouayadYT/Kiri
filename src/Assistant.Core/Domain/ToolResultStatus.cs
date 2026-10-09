using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>How a <see cref="ToolCall"/> ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolResultStatus>))]
public enum ToolResultStatus
{
    /// <summary>The tool ran and returned its output.</summary>
    Succeeded = 0,

    /// <summary>The call was invalid or not permitted, or the tool failed or timed out.</summary>
    Failed = 1,

    /// <summary>The user declined the confirmation, so the tool did not run.</summary>
    Declined = 2,

    /// <summary>The request was cancelled before the tool finished.</summary>
    Cancelled = 3,
}
