using System.Text.Json;

namespace Assistant.Core.Assets;

/// <summary>One file a packaged asset consists of, and what it must be.</summary>
/// <param name="Path">The file's path inside its group's folder, with <c>/</c> as the separator, never absolute and never leaving the folder.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, as 64 lower-case hexadecimal digits.</param>
public sealed record AssetFile(string Path, long Size, string Sha256);

/// <summary>The files of one asset: a model profile's, or a text-to-speech engine's. They live in a folder named for the group.</summary>
/// <param name="Id">The group's identifier: lower case letters, digits and hyphens, the same as the profile's or the engine's.</param>
/// <param name="Files">Every file the asset needs.</param>
public sealed record AssetGroup(string Id, IReadOnlyList<AssetFile> Files);

/// <summary>
/// What a folder of packaged assets should contain (PROJECT_SPEC §3.5, step 123): for each group, every file with its size and SHA-256. It is read
/// with <see cref="AssetManifestReader"/> and checked with <see cref="PackagedAssets"/> before a model is loaded.
/// </summary>
/// <param name="Groups">The groups, in the order the manifest lists them.</param>
public sealed record AssetManifest(IReadOnlyList<AssetGroup> Groups)
{
    /// <summary>The version of the manifest format this build reads and writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The group with identifier <paramref name="id"/>, or <see langword="null"/> when the manifest has none.</summary>
    public AssetGroup? Find(string id) => Groups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));

    /// <summary>The manifest as JSON, the way the packaging script writes it.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("groups");
            foreach (var group in Groups)
            {
                writer.WriteStartObject();
                writer.WriteString("id", group.Id);
                writer.WriteStartArray("files");
                foreach (var file in group.Files)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", file.Path);
                    writer.WriteNumber("size", file.Size);
                    writer.WriteString("sha256", file.Sha256);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
