using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Data.Migrations;

/// <summary>
/// Finds the migrations that are embedded in an assembly as <c>Migrations/Scripts/NNNN_name.sql</c> files. Adding a
/// migration is adding a file, so one cannot be forgotten in a list.
/// </summary>
internal static partial class MigrationScripts
{
    /// <summary>The logical resource-name prefix the project gives the scripts (see Assistant.Data.csproj).</summary>
    public const string ResourcePrefix = "migrations/";

    [GeneratedRegex(@"^migrations/(?<version>\d{4})_(?<name>[a-z][a-z0-9_]*)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceNamePattern();

    /// <summary>Reads every embedded migration script of <paramref name="assembly"/>, lowest version first.</summary>
    /// <exception cref="InvalidOperationException">An embedded script is not named <c>NNNN_name.sql</c>.</exception>
    public static IReadOnlyList<Migration> Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var migrations = new List<Migration>();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        foreach (var resourceName in resourceNames)
        {
            if (!TryParseName(resourceName, out var version, out var name))
            {
                throw new InvalidOperationException(
                    $"The embedded migration '{resourceName}' is not named like {ResourcePrefix}0001_name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The embedded migration '{resourceName}' cannot be read.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(new Migration(version, name, reader.ReadToEnd()));
        }

        return migrations;
    }

    /// <summary>Reads the version and name out of a resource name such as <c>migrations/0001_initial_schema.sql</c>.</summary>
    public static bool TryParseName(string resourceName, out int version, out string name)
    {
        var match = ResourceNamePattern().Match(resourceName);
        if (!match.Success)
        {
            version = 0;
            name = string.Empty;
            return false;
        }

        version = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
        name = match.Groups["name"].Value;
        return true;
    }
}
