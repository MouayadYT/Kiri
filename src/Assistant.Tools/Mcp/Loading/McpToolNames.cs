using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Tools;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The names the model calls a connected app's tools by (step 104). The tool registry names tools in lower snake_case of at most 64 characters, an
/// MCP server names its tools as it pleases (<c>getUser</c>, <c>admin.tools.list</c>), and two apps may each have a <c>search</c>. A tool is named
/// <c>mcp_&lt;app&gt;_&lt;tool&gt;</c>: the prefix (<see cref="ConnectedAppTools.Prefix"/>) tells it from a built-in one and from another app's, the
/// rest is made from the app's id and the server's name for the tool, and a name that is too long or that another tool already has ends in six
/// hexadecimal digits made from the original, so the same tool always gets the same name. The app is one word (an id is letters and digits
/// only): the registry's rule against names that read as a command looks at a name word by word, and an app called "Terminal Notifier" must not
/// lose its tools to the word in its name. An id that is itself such a word gets "app" after it, and an id too long for a name is cut and
/// ends in four hexadecimal digits of the whole id, so two long ids that begin alike are never given the same tool names.
/// </summary>
internal static partial class McpToolNames
{
    /// <summary>The longest tool name the registry accepts.</summary>
    public const int MaxLength = 64;

    private const int MaxAppPart = 20;
    private const int AppHashLength = 4;
    private const int HashLength = 6;

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Whether <paramref name="name"/> is a name the tool registry accepts.</summary>
    public static bool IsRegistryName(string name) => name.Length <= MaxLength && NamePattern().IsMatch(name);

    /// <summary>The name of the tool <paramref name="serverToolName"/> of the app <paramref name="integrationId"/>, when no other tool has taken it.</summary>
    public static string Create(string integrationId, string serverToolName) => Build(integrationId, serverToolName, forceHash: false);

    /// <summary>As <see cref="Create"/>, but always ending in the hexadecimal digits that tell it from another tool whose name came out the same.</summary>
    public static string CreateDistinct(string integrationId, string serverToolName) => Build(integrationId, serverToolName, forceHash: true);

    // The app as one word of a name.
    private static string AppWord(string integrationId)
    {
        var word = McpText.Snake(integrationId).Replace("_", string.Empty, StringComparison.Ordinal);
        if (word.Length > MaxAppPart)
        {
            var whole = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(integrationId)))[..AppHashLength].ToLowerInvariant();
            word = word[..(MaxAppPart - AppHashLength)] + whole;
        }

        if (word.Length == 0)
        {
            word = "app";
        }

        return ToolDefinitionGuard.ReadsAsExecution(word) ? word + "app" : word;
    }

    private static string Build(string integrationId, string serverToolName, bool forceHash)
    {
        var app = AppWord(integrationId);
        var tool = McpText.Snake(serverToolName);
        var prefix = ConnectedAppTools.Prefix + app + "_";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(integrationId + "\n" + serverToolName)))[..HashLength].ToLowerInvariant();
        if (tool.Length == 0)
        {
            return prefix + "tool_" + hash;
        }

        if (!forceHash && prefix.Length + tool.Length <= MaxLength)
        {
            return prefix + tool;
        }

        var room = MaxLength - prefix.Length - HashLength - 1;
        var kept = tool.Length > room ? tool[..room].TrimEnd('_') : tool;
        return prefix + (kept.Length == 0 ? "tool" : kept) + "_" + hash;
    }
}
