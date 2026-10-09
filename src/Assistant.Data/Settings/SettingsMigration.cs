using System.Text.Json.Nodes;

namespace Assistant.Data.Settings;

/// <summary>
/// One step that brings a settings document written by schema version <paramref name="FromVersion"/> up to the next
/// version. It works on the JSON as it was written, before it becomes <c>AppSettings</c>, so it can rename, move or
/// convert a value the current types no longer have. The reader sets the document's version once the step has run.
/// </summary>
/// <param name="FromVersion">The version the step reads.</param>
/// <param name="Apply">Changes the document in place.</param>
public sealed record SettingsMigration(int FromVersion, Action<JsonObject> Apply);
