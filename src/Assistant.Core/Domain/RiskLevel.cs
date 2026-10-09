using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>
/// How much a tool can change. Fixed in code; model output and captured content can never change it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    /// <summary>Only reads data. Runs automatically.</summary>
    ReadOnly = 0,

    /// <summary>Changes state. Runs only after the user confirms.</summary>
    SideEffect = 1,

    /// <summary>Can destroy data. Always rejected.</summary>
    Destructive = 2,
}
