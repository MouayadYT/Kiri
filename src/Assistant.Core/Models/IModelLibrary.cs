namespace Assistant.Core.Models;

public sealed record ModelDownloadProgress(long ReceivedBytes, long TotalBytes, string Stage, double BytesPerSecond = 0)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(100d * ReceivedBytes / TotalBytes, 0, 100) : 0;
}

public interface IModelLibrary
{
    string FolderOf(DownloadableModel model);
    bool IsInstalled(DownloadableModel model);
    bool HasManagedFiles(DownloadableModel model) => IsInstalled(model);

    /// <summary>
    /// How many bytes a download of <paramref name="model"/> would fetch now: nothing for one that is installed, all of it for one that is not, and
    /// only what is missing for one that was installed before the catalog gave it another file (a vision projector).
    /// </summary>
    long BytesToDownload(DownloadableModel model) => IsInstalled(model) ? 0 : model.DownloadBytes;
    Task DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken = default);
    Task DeleteAsync(DownloadableModel model, CancellationToken cancellationToken = default);
}
