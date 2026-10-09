namespace Assistant.Windows.Gaming;

/// <summary>
/// The creative apps that game mode treats like a game while one is open, by the names people know them by (Settings > General, "View the full list"):
/// the video, 3D, CAD and photo programs that need the graphics card the way a game does. <see cref="GameCatalog"/> knows them by their programs' names,
/// which are not what anyone calls them ("afterfx"); this is for the user to read.
/// </summary>
public static class CreativeAppNames
{
    /// <summary>The names, in the order they are listed: video, then 3D and CAD, then photo and painting.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "Adobe Premiere Pro", "Adobe Premiere Elements", "Adobe After Effects", "Adobe Media Encoder", "Adobe Character Animator", "DaVinci Resolve", "CapCut",
        "Filmora", "Kdenlive", "Shotcut", "HandBrake", "Avid Media Composer", "EDIUS", "HitFilm", "Olive", "Vegas Pro", "Nuke", "Topaz Video AI",
        "Topaz Photo AI", "Topaz Gigapixel AI",
        "Blender", "Cinema 4D", "Maya", "3ds Max", "Houdini", "ZBrush", "Marmoset Toolbag", "KeyShot", "Unreal Editor", "Unity", "Twinmotion", "Lumion",
        "D5 Render", "Octane", "Modo", "Substance 3D Painter, Designer, Sampler and Stager", "Marvelous Designer", "AutoCAD", "SOLIDWORKS", "Fusion 360",
        "SketchUp", "Rhino", "Revit", "Inventor", "ArchiCAD",
        "Adobe Photoshop", "Adobe Illustrator", "Adobe Lightroom", "Krita", "Clip Studio Paint", "Capture One", "Affinity Photo, Designer and Publisher",
        "DxO PhotoLab", "Luminar",
    ];
}