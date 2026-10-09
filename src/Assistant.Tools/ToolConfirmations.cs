using System.Globalization;
using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;

namespace Assistant.Tools;

/// <summary>
/// The question the executor asks the user about a call whose tool did not say what to ask (PROJECT_SPEC §4.8, step 115): the tool's name (and the app's, for a connected
/// app's tool) in the title and every argument of the call, as it was given, one line each, so nothing the call carries is hidden from the person who approves it. It is what every tool of a connected app falls back on,
/// and any tool added later that does not word its own question. Text that is not the Assistant's own is made safe to show (<see cref="ConfirmationText"/>).
/// </summary>
public static class ToolConfirmations
{
    /// <summary>
    /// The question for a call of <paramref name="definition"/> with <paramref name="arguments"/>: every argument on a line of its own, with the value it has.
    /// <see langword="null"/> when the call carries more than a person can be asked to read (<see cref="ToolConfirmation.MaxTotalLength"/>), which is then not made: a user
    /// is never asked to approve what they cannot see.
    /// </summary>
    /// <param name="definition">The tool.</param>
    /// <param name="arguments">The call's arguments, a JSON object.</param>
    /// <param name="kind">What kind of thing the tool does.</param>
    /// <param name="app">The connected app the tool belongs to, when it is one's, named in the title; <see langword="null"/> otherwise.</param>
    /// <param name="toolName">The name to show for the tool, when it is not the definition's (a connected app's tool is shown by the name the app gave it).</param>
    public static ToolConfirmation? ForCall(
        ToolDefinition definition, JsonElement arguments, ConfirmationKind kind = ConfirmationKind.Other, string? app = null, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // The tool and the app are in the title, which is the Assistant's own words; the lines are only the call's arguments, so that an argument that is
        // named like something else (an "app" argument) cannot pass for a line the Assistant wrote.
        var lines = new List<ConfirmationDetail>();
        var any = false;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var argument in arguments.EnumerateObject())
            {
                any = true;
                lines.Add(new ConfirmationDetail(Humanize(argument.Name), Show(argument.Value)));
            }
        }

        if (!any)
        {
            lines.Add(new ConfirmationDetail("Details", "None: it takes no arguments."));
        }

        var name = ConfirmationText.Short(toolName ?? definition.Name, 60);
        var title = string.IsNullOrWhiteSpace(app) ? $"Allow “{name}”?" : $"Allow “{name}” in {ConfirmationText.Short(app, 40)}?";
        try
        {
            return new ToolConfirmation(
                kind, title, lines, warning: "The Assistant cannot take this back once it is done.");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // "list_id" reads "List id": an argument's name, as a label.
    private static string Humanize(string name)
    {
        var words = name.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }

    // A text as it is; a number, a boolean or anything else as it was written, so no argument is shown as other than it was given.
    private static string Show(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
