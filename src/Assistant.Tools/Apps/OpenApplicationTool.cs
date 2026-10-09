using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.Tools;

namespace Assistant.Tools.Apps;

/// <summary>
/// <c>open_application</c> (PROJECT_SPEC §4.8): starts an application that is installed on this PC, by the name Start shows for it. The
/// model gives a name and nothing else: what is started is always an entry of the list Windows gave (<see cref="IApplicationCatalog"/>),
/// by the launch identity that list holds, so no program, path, command line or argument that the model writes is ever run. A name that
/// fits more than one application is not guessed: the model is given the candidates and asks the user. It has a side effect, so the
/// user confirms each call, with the name shown.
/// </summary>
public sealed class OpenApplicationTool(IApplicationCatalog applications, IApplicationLauncher launcher) : ITool
{
    /// <summary>The most candidate names a result lists when a name fits several applications.</summary>
    public const int MaxCandidates = 5;

    /// <summary>The longest an application's name may be, as the model gives it.</summary>
    public const int MaxNameLength = 100;

    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        SystemToolResults.OpenApplication,
        "Open an application that is installed on this PC, by the name Start shows for it, such as Calculator, Notepad or Settings. It " +
        "cannot run commands or programs by path. To open a file use open_file, and for a folder open_folder.",
        [
            new ToolParameter(
                "application",
                ToolParameterType.String,
                "The name of the application, as the user says it, such as Calculator or Visual Studio Code.",
                MaxLength: MaxNameLength),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(20));

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (application, refusal) = await FindAsync(call, arguments, cancellationToken).ConfigureAwait(false);
        return refusal ?? Launch(call, application!);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The name is matched before the user is asked, so a name that fits no application, or two, is told to the model and nobody is asked about it; the user is
    /// shown the one application that matched, and it is that one, by its launch identity, that is started.
    /// </remarks>
    public async Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (application, refusal) = await FindAsync(call, arguments, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return ToolPlan.Refuse(refusal);
        }

        var found = application!;
        var details = new List<ConfirmationDetail> { new("Application", found.DisplayName) };
        if (!string.IsNullOrWhiteSpace(found.ExecutablePath))
        {
            details.Add(new ConfirmationDetail("Program", found.ExecutablePath));
        }

        return ToolPlan.Do(_ => Task.FromResult(Launch(call, found)), new ToolConfirmation(ConfirmationKind.Launch, "Open this application?", details, "Open"));
    }

    // The one application the call's name means, or why none is started.
    private async Task<(InstalledApplication? Application, ToolResult? Refusal)> FindAsync(ToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var name = arguments.GetProperty("application").GetString() ?? string.Empty;
        if (QuickSearchText.Normalize(name).Length == 0)
        {
            return (null, Failed(call, "The application's name is empty. Say which application to open."));
        }

        var installed = await applications.GetApplicationsAsync(cancellationToken).ConfigureAwait(false);
        var matches = Match(name, installed);
        switch (matches.Count)
        {
            case 0:
                return (null, Failed(call, "No installed application has that name. Tell the user, or ask what they meant."));
            case > 1:
                var names = string.Join(", ", matches.Take(MaxCandidates).Select(match => match.DisplayName));
                return (null, Failed(call, $"More than one application fits that name: {names}. Ask the user which one."));
        }

        return (matches[0], null);
    }

    private ToolResult Launch(ToolCall call, InstalledApplication application) =>
        launcher.Launch(application.Id)
            ? new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, SystemToolResults.Done($"Opened {application.DisplayName}."))
            : Failed(call, $"{application.DisplayName} could not be started. Tell the user.");

    /// <summary>
    /// The applications that <paramref name="name"/> means: those whose name is it, else those whose name begins with it, else those that
    /// have it as the beginnings of their words, else those that contain it, taking the first of these that is not empty, and one of each
    /// distinct name (a program listed twice by Start is one application).
    /// </summary>
    internal static List<InstalledApplication> Match(string name, IReadOnlyList<InstalledApplication> installed)
    {
        var typed = QuickSearchText.Normalize(name);
        var typedWords = typed.Split(' ');
        var tiers = new List<InstalledApplication>[4];
        for (var tier = 0; tier < tiers.Length; tier++)
        {
            tiers[tier] = [];
        }

        foreach (var application in installed)
        {
            var title = QuickSearchText.Normalize(application.DisplayName);
            if (title.Length == 0)
            {
                continue;
            }

            if (title == typed)
            {
                tiers[0].Add(application);
            }
            else if (title.StartsWith(typed, StringComparison.Ordinal))
            {
                tiers[1].Add(application);
            }
            else if (BeginsWords(typedWords, title.Split(' ')))
            {
                tiers[2].Add(application);
            }
            else if (title.Contains(typed, StringComparison.Ordinal))
            {
                tiers[3].Add(application);
            }
        }

        var best = tiers.FirstOrDefault(tier => tier.Count > 0) ?? [];
        return [.. best.DistinctBy(application => QuickSearchText.Normalize(application.DisplayName), StringComparer.Ordinal)];
    }

    // Each typed word begins a different word of the name.
    private static bool BeginsWords(string[] typedWords, string[] nameWords)
    {
        var used = new bool[nameWords.Length];
        foreach (var typed in typedWords)
        {
            var found = false;
            for (var index = 0; index < nameWords.Length; index++)
            {
                if (!used[index] && nameWords[index].StartsWith(typed, StringComparison.Ordinal))
                {
                    used[index] = true;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static ToolResult Failed(ToolCall call, string message) =>
        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);
}
