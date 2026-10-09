using System.Text.Json.Serialization;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Whether the inference runtime bundled with the app can run (PROJECT_SPEC §5.6), as the model host found it. Its
/// wording for the user comes from <see cref="ModelRuntimeStateText"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelRuntimeState>))]
public enum ModelRuntimeState
{
    /// <summary>The runtime and every native file it needs are in place.</summary>
    Ready = 0,

    /// <summary>The runtime's folder or its executable is not in the installed app.</summary>
    NotInstalled = 1,

    /// <summary>The executable is there, but native files it needs are missing.</summary>
    Incomplete = 2,

    /// <summary>A runtime file is damaged, or not built for 64-bit (x64) Windows.</summary>
    Incompatible = 3,

    /// <summary>The runtime is complete, but a Windows component it needs, the Visual C++ runtime, is not installed.</summary>
    MissingSystemComponent = 4,

    /// <summary>A runtime file could not be read, for example because security software holds it.</summary>
    Inaccessible = 5,
}
