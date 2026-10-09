using System.Text.Json.Serialization;
using Assistant.Core.Tools;

namespace Assistant.Core.Domain;

/// <summary>
/// Describes a tool the model may call (PROJECT_SPEC §4.8): its stable name, what it does, the arguments it takes, how much it can
/// change, the permission it needs, and how long it may run. What the model is told and what the registry checks are fixed in code;
/// nothing the model or a file says can add a tool or change one. The code a tool runs is its handler (<c>ITool</c>), which lives with
/// the tools, not here.
/// </summary>
/// <param name="Name">Stable snake_case name the model uses to call the tool.</param>
/// <param name="Description">Model-facing description of what the tool does.</param>
/// <param name="InputSchemaJson">JSON Schema of the tool's input: an object of typed properties, made from <see cref="ToolParameter"/>s by <see cref="Create"/>.</param>
/// <param name="RiskLevel">
/// The tool's side-effect category: decides whether the tool runs automatically (<see cref="Domain.RiskLevel.ReadOnly"/>), needs the
/// user's confirmation each time (<see cref="Domain.RiskLevel.SideEffect"/>), or is never registered or run
/// (<see cref="Domain.RiskLevel.Destructive"/>).
/// </param>
public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson, RiskLevel RiskLevel)
{
    /// <summary>How long a call may run when the definition does not say.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest a tool may be given to run; a call that takes longer than its time is given up as a failure.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The permission (Settings, Permissions) the tool needs: a call is refused, before the tool runs, while the user has it off.
    /// <see langword="null"/> for a tool that touches nothing the permissions guard. It is not part of what the model is told, and not on the wire.
    /// </summary>
    [JsonIgnore]
    public PermissionCapability? RequiredPermission { get; init; }

    /// <summary>
    /// Whether a tool that changes something runs without the user being asked each time: only for one of the Assistant's own tools that does a small thing the
    /// user asked for in words and can switch back at once (Do not disturb, the sound, a note it keeps). It is fixed in code like the risk level, a connected
    /// app's tool never has it, and the step is still shown and kept in the activity log. Not on the wire.
    /// </summary>
    [JsonIgnore]
    public bool RunsWithoutAsking { get; init; }

    /// <summary>How long a call may run; <see langword="null"/> for <see cref="DefaultTimeout"/>. Not on the wire.</summary>
    [JsonIgnore]
    public TimeSpan? Timeout { get; init; }

    /// <summary>How long a call may run: <see cref="Timeout"/>, or <see cref="DefaultTimeout"/>.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveTimeout => Timeout ?? DefaultTimeout;

    /// <summary>Describes a tool by its typed arguments, from which the input schema is made.</summary>
    /// <exception cref="ArgumentException">The arguments are not valid (<see cref="ToolSchema.Build"/>).</exception>
    public static ToolDefinition Create(
        string name, string description, IEnumerable<ToolParameter> parameters, RiskLevel riskLevel,
        PermissionCapability? requiredPermission = null, TimeSpan? timeout = null, bool runsWithoutAsking = false) =>
        new(name, description, ToolSchema.Build(parameters), riskLevel)
        {
            RequiredPermission = requiredPermission,
            Timeout = timeout,
            RunsWithoutAsking = runsWithoutAsking,
        };
}
