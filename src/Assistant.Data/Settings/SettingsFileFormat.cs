using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Settings;

namespace Assistant.Data.Settings;

/// <summary>What was read from a settings file.</summary>
/// <param name="Settings">The settings, every value valid.</param>
/// <param name="FileVersion">The schema version the file said it had.</param>
/// <param name="ReplacedValues">How many values were not readable or not allowed and are back to their defaults.</param>
internal sealed record ParsedSettings(AppSettings Settings, int FileVersion, int ReplacedValues);

/// <summary>
/// The settings file's format (PROJECT_SPEC §5.10): one JSON object with a <c>schemaVersion</c> and a member for each
/// section of <see cref="AppSettings"/>, in camel case, indented so a person can read it. Reading is forgiving one value
/// at a time: a value that is missing gets its default silently, and one that is the wrong type, out of range or not
/// allowed gets it too (and is counted), so one bad value never costs the user the rest of their settings. Only a file
/// that is not a settings document at all is refused.
/// </summary>
internal static class SettingsFileFormat
{
    /// <summary>The largest settings file that is read, in bytes. The real one is a few kilobytes.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The bytes of the file that holds <paramref name="settings"/>: UTF-8, without a byte order mark.</summary>
    public static byte[] Serialize(AppSettings settings) =>
        JsonSerializer.SerializeToUtf8Bytes(settings with { SchemaVersion = AppSettings.CurrentSchemaVersion }, Options);

    /// <summary>
    /// Reads a settings file. Returns <see langword="false"/> when <paramref name="bytes"/> are not a settings document:
    /// not JSON, not an object, no usable <c>schemaVersion</c>, or a version this build has no migration from.
    /// <paramref name="currentVersion"/> is the version to migrate up to: the build's own, except in a test of the
    /// migrations, which none of the versions so far needs.
    /// </summary>
    public static bool TryParse(
        byte[] bytes, IReadOnlyList<SettingsMigration> migrations, out ParsedSettings? parsed,
        int currentVersion = AppSettings.CurrentSchemaVersion)
    {
        parsed = null;
        if (bytes.Length is 0 or > MaxFileBytes)
        {
            return false;
        }

        JsonObject document;
        try
        {
            if (JsonNode.Parse(bytes, NodeOptions, DocumentOptions) is not JsonObject root)
            {
                return false;
            }

            document = root;
        }
        catch (JsonException)
        {
            return false;
        }

        if (!document.TryGetPropertyValue("schemaVersion", out var versionNode)
            || versionNode is not JsonValue versionValue
            || !versionValue.TryGetValue<int>(out var version)
            || version < 1)
        {
            return false;
        }

        // A file from a newer build is read as far as this build understands it. One from an older build is brought up a
        // version at a time, and refused if a step is missing, which would be a defect worth finding.
        for (var from = version; from < currentVersion; from++)
        {
            var step = migrations.FirstOrDefault(migration => migration.FromVersion == from);
            if (step is null)
            {
                return false;
            }

            try
            {
                step.Apply(document);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
            {
                return false;
            }

            document["schemaVersion"] = from + 1;
        }

        var replaced = 0;
        var read = (AppSettings)ReadObject(typeof(AppSettings), document, ref replaced);
        var settings = SettingsValidator.Sanitize(read with { SchemaVersion = AppSettings.CurrentSchemaVersion }, out var repaired);
        parsed = new ParsedSettings(settings, version, replaced + repaired.Count);
        return true;
    }

    // Reads the object a section or the document is, member by member, keeping the default of any member that is missing
    // or cannot be read. A member is set through reflection, since the settings are records with init-only properties.
    private static object ReadObject(Type type, JsonObject json, ref int replaced)
    {
        var target = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is null || !json.TryGetPropertyValue(ToCamelCase(property.Name), out var node))
            {
                continue;
            }

            if (node is null)
            {
                // An explicit null: for a shortcut, "off". A value type cannot be null, so it keeps its default.
                if (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null)
                {
                    property.SetValue(target, null);
                }
                else
                {
                    replaced++;
                }

                continue;
            }

            if (IsSection(property.PropertyType))
            {
                if (node is JsonObject nested)
                {
                    property.SetValue(target, ReadObject(property.PropertyType, nested, ref replaced));
                }
                else
                {
                    replaced++;
                }

                continue;
            }

            try
            {
                property.SetValue(target, node.Deserialize(property.PropertyType, Options));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
                or NotSupportedException or ArgumentException or OverflowException)
            {
                replaced++;
            }
        }

        return target;
    }

    // A section is one of the settings records that build themselves with their defaults; a shortcut (which needs its
    // keys), a list, an enum or a number is one value.
    private static bool IsSection(Type type) =>
        type.IsClass && type.Namespace == typeof(AppSettings).Namespace && type.GetConstructor(Type.EmptyTypes) is not null;

    private static string ToCamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}
