using Assistant.Core.Models;
using Assistant.UI.Settings;

namespace Assistant.UI.Onboarding;

public sealed class ModelChoice(DownloadableModel model, IModelLibrary library) : NotifyingObject
{
    public DownloadableModel Model { get; } = model;
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Detail => Model.Detail;
    public string Ram => Model.MemoryText;
    public string Size => AddsVision ? Megabytes(library.BytesToDownload(Model)) + " to add · " + Model.DownloadSize + " in all" : Model.DownloadSize + " download";
    public bool Recommended => Model.Recommended;
    public bool IsInstalled => library.IsInstalled(Model);
    public bool CanDelete => library.HasManagedFiles(Model);
    public bool NeedsUpdate => CanDelete && !IsInstalled;

    /// <summary>
    /// Whether the model is on this PC as it was first offered, without the vision projector the catalog has since given it: a download then
    /// fetches the projector alone, and the model reads pictures from then on.
    /// </summary>
    public bool AddsVision => NeedsUpdate && Model.Projector is { } projector && library.BytesToDownload(Model) == projector.Bytes;
    public string State => AddsVision ? "Update available · download to let this model read pictures (" + Megabytes(Model.Projector!.Bytes) + ")"
        : NeedsUpdate ? "Update available · download the corrected model" : IsInstalled ? "Downloaded" : "Not downloaded";
    public string Label => Name + " · " + Detail.Split('·')[0].Trim();
    public void Refresh() { OnPropertyChanged(nameof(IsInstalled)); OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(NeedsUpdate)); OnPropertyChanged(nameof(AddsVision)); OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(Size)); }
    private static string Megabytes(long bytes) => (bytes / 1_000_000d).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " MB";
}
