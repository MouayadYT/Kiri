using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;

namespace Assistant.UI.Settings;

/// <summary>
/// Context: how much one request may include. The room kept for the answer takes effect now; how many files may be attached
/// or retrieved and the largest file read wait for file context (PROJECT_SPEC §4.4, §4.7), so those controls show what is
/// saved and cannot be changed yet.
/// </summary>
public sealed class ContextPage : SettingsPage
{
    public NumberField NormalLimit => _rootModel.NormalLimit;
    public NumberField HeavyLimit => _rootModel.HeavyLimit;
    private readonly ModelPage _rootModel;
    internal ContextPage(SettingsViewModel root)
        : base(root, SettingsSection.Context)
    {
        _rootModel = root.Model;
        ReservedForAnswer = new NumberField(AssessReserve, tokens => Commit(settings => settings with
        {
            ContextLimits = settings.ContextLimits with { ReservedOutputTokens = tokens },
        }));
        MaxAttachedFiles = new NumberField(
            files => Range(files, SettingsLimits.MinAttachedFiles, SettingsLimits.MaxAttachedFiles),
            files => Commit(settings => settings with { ContextLimits = settings.ContextLimits with { MaxAttachedFiles = files } }));
        MaxRetrievedFiles = new NumberField(
            files => Range(files, SettingsLimits.MinRetrievedFiles, SettingsLimits.MaxRetrievedFiles),
            files => Commit(settings => settings with { ContextLimits = settings.ContextLimits with { MaxRetrievedFiles = files } }));
        MaxFileSizeMegabytes = new NumberField(
            megabytes => Range(megabytes, (int)(SettingsLimits.MinFileSizeBytes / Megabyte), (int)(SettingsLimits.MaxFileSizeBytes / Megabyte)),
            megabytes => Commit(settings => settings with
            {
                ContextLimits = settings.ContextLimits with { MaxFileSizeBytes = megabytes * Megabyte },
            }));
    }

    private const long Megabyte = 1024L * 1024;

    /// <summary>Tokens of the context window kept back for the answer, or 0 for the default.</summary>
    public NumberField ReservedForAnswer { get; }

    /// <summary>The most files one request may have attached.</summary>
    public NumberField MaxAttachedFiles { get; }

    /// <summary>The most files retrieved from Windows Search to ground one answer.</summary>
    public NumberField MaxRetrievedFiles { get; }

    /// <summary>The largest file whose text is read, in megabytes.</summary>
    public NumberField MaxFileSizeMegabytes { get; }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        var limits = settings.ContextLimits;
        Show(ReservedForAnswer, limits.ReservedOutputTokens, fresh);
        Show(MaxAttachedFiles, limits.MaxAttachedFiles, fresh);
        Show(MaxRetrievedFiles, limits.MaxRetrievedFiles, fresh);
        Show(MaxFileSizeMegabytes, (int)(limits.MaxFileSizeBytes / Megabyte), fresh);
    }

    private ContextAdvice AssessReserve(int tokens)
    {
        if (tokens == 0)
        {
            return new ContextAdvice(ContextAdviceLevel.Note, "The default: 1,024 tokens.");
        }

        if (tokens is < SettingsLimits.MinReservedOutputTokens or > SettingsLimits.MaxReservedOutputTokens)
        {
            return new ContextAdvice(
                ContextAdviceLevel.Error,
                $"Enter 0 for the default, or a number from {SettingsLimits.MinReservedOutputTokens} to {NumberField.Format(SettingsLimits.MaxReservedOutputTokens)}.");
        }

        var normal = Current.ContextLimits.NormalContextTokens;
        return normal > 0 && tokens > normal / 2
            ? new ContextAdvice(
                ContextAdviceLevel.Note,
                "The answer never gets more than half of a conversation's limit, so a smaller amount is kept back when this is more.")
            : ContextAdvice.None;
    }

    private static ContextAdvice Range(int value, int min, int max) =>
        value >= min && value <= max
            ? ContextAdvice.None
            : new ContextAdvice(ContextAdviceLevel.Error, $"Enter a number from {NumberField.Format(min)} to {NumberField.Format(max)}.");

    private static void Show(NumberField field, int value, bool fresh)
    {
        if (fresh)
        {
            field.Reset(value);
        }
        else
        {
            field.Show(value);
        }
    }
}
