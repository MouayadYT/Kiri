using System.Text.Json;

namespace Assistant.Tools.Mcp;

/// <summary>What a server said it can do.</summary>
/// <param name="Tools">It offers tools.</param>
/// <param name="ToolsListChanged">It says when its list of tools changes.</param>
/// <param name="Resources">It offers resources (not used).</param>
/// <param name="Prompts">It offers prompts (not used).</param>
public sealed record McpServerCapabilities(bool Tools, bool ToolsListChanged, bool Resources, bool Prompts)
{
    /// <summary>A server that offers nothing.</summary>
    public static McpServerCapabilities None { get; } = new(false, false, false, false);
}

/// <summary>What the Assistant learned about a server when it connected. The server's own instructions for models are not kept: they are third-party text meant to steer a model.</summary>
/// <param name="Name">What the server calls itself, at most 100 characters, or <see langword="null"/>. Self-reported and unverified: for display only.</param>
/// <param name="Version">The version it reports, at most 64 characters, or <see langword="null"/>.</param>
/// <param name="ProtocolVersion">The protocol version agreed.</param>
/// <param name="Capabilities">What it can do.</param>
public sealed record McpServerInfo(string? Name, string? Version, string ProtocolVersion, McpServerCapabilities Capabilities);

/// <summary>What a server says about how a tool behaves. Hints, never to be trusted unless the user trusts the server (the protocol says so itself).</summary>
/// <param name="ReadOnlyHint">The tool says it changes nothing.</param>
/// <param name="DestructiveHint">The tool says it may destroy data.</param>
/// <param name="IdempotentHint">The tool says calling it again with the same arguments has no further effect.</param>
/// <param name="OpenWorldHint">The tool says it reaches things outside the app.</param>
public sealed record McpToolAnnotations(bool? ReadOnlyHint, bool? DestructiveHint, bool? IdempotentHint, bool? OpenWorldHint)
{
    /// <summary>A tool that says nothing about itself.</summary>
    public static McpToolAnnotations None { get; } = new(null, null, null, null);
}

/// <summary>One tool of a server, as it listed it. Everything here except <see cref="Name"/> is third-party text, which is cleaned before a model sees it.</summary>
/// <param name="Name">The tool's name (letters, digits, underscore, hyphen and dot; at most 128 characters).</param>
/// <param name="Title">A name for people, or <see langword="null"/>.</param>
/// <param name="Description">What it does, cut to <see cref="McpToolDescriptor.MaxDescriptionLength"/> characters, or <see langword="null"/>.</param>
/// <param name="InputSchema">The JSON Schema of its arguments, an object.</param>
/// <param name="Annotations">What it says about how it behaves.</param>
public sealed record McpToolDescriptor(string Name, string? Title, string? Description, JsonElement InputSchema, McpToolAnnotations Annotations)
{
    /// <summary>The most characters of a description that are kept.</summary>
    public const int MaxDescriptionLength = 2000;

    /// <summary>The most characters of a title that are kept.</summary>
    public const int MaxTitleLength = 200;
}

/// <summary>The kinds of content a tool's result can hold.</summary>
public enum McpContentKind
{
    /// <summary>Text.</summary>
    Text = 0,

    /// <summary>A picture (not shown to the model).</summary>
    Image = 1,

    /// <summary>Sound (not shown to the model).</summary>
    Audio = 2,

    /// <summary>A link to a resource (named, never followed).</summary>
    ResourceLink = 3,

    /// <summary>A resource that came with the result: its text, when it is text.</summary>
    Resource = 4,
}

/// <summary>One piece of a tool's result.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Text">The text of <see cref="McpContentKind.Text"/>, or of an embedded text <see cref="McpContentKind.Resource"/>; <see langword="null"/> otherwise.</param>
/// <param name="MimeType">The type of an image, sound or resource, when given.</param>
/// <param name="Uri">The address of a resource (a link or an embedded one), when given. It is named to the model, never opened.</param>
/// <param name="Name">The name of a resource link, when given.</param>
public sealed record McpContentBlock(McpContentKind Kind, string? Text, string? MimeType, string? Uri, string? Name);

/// <summary>What a tool returned.</summary>
/// <param name="IsError">The tool says that it failed (the model is told why and may correct its call).</param>
/// <param name="Content">The pieces of the result, in order.</param>
/// <param name="StructuredContent">The result as JSON when the tool gave it that way, or <see langword="null"/>.</param>
public sealed record McpToolResult(bool IsError, IReadOnlyList<McpContentBlock> Content, JsonElement? StructuredContent);
