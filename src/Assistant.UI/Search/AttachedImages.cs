using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.Messages;

namespace Assistant.UI.Search;

/// <summary>What reading the images attached to a question came to: the context to ask with, or why there is none.</summary>
/// <param name="Items">The images, as context the model is given; empty when there was a problem.</param>
/// <param name="Problem">What to tell the user when an image could not be read, so nothing is asked; otherwise <see langword="null"/>.</param>
internal sealed record AttachedImagesResult(IReadOnlyList<ContextItem> Items, string? Problem);

/// <summary>
/// Reads the images the user attached to a question, from the files they point to, into context for the model
/// (<see cref="ContextItemType.Image"/>). Reading a file needs the Files permission (PROJECT_SPEC §4.9): with it off, or with no
/// policy to ask, nothing is read and the user is told so. An image that is gone, unreadable or too large is a problem too, and
/// the question is not asked without it, since the answer would be about something else. The bytes live in memory only, and
/// neither they nor a path are logged.
/// </summary>
internal sealed class AttachedImages(IPermissionPolicy? permissions)
{
    /// <summary>The largest image file that is read, in bytes; the pipeline scales it down before the model sees it.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>What the user is told when the Files permission is off.</summary>
    public const string FilesTurnedOffText =
        "Files are turned off in Settings, under Permissions, so the attached image wasn't read and nothing was asked. Turn Files on there, then ask again.";

    /// <summary>What the user is told when there is no way to check the Files permission.</summary>
    public const string NotAvailableText = "Attaching files isn't available here, so nothing was asked.";

    /// <summary>What the user is told when an attached image cannot be read.</summary>
    public const string UnreadableText =
        "An attached image couldn't be opened, so nothing was asked. It may have been moved, deleted or be too large.";

    /// <summary>Reads <paramref name="images"/> into context, or says why that cannot be done.</summary>
    public async Task<AttachedImagesResult> ReadAsync(IReadOnlyList<ImageItem> images, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);

        // A part of the screen is not a file: it was captured with the Screen Capture permission, and its picture is in the context
        // service, which the question's context is taken from; nothing is read for it here.
        images = [.. images.Where(image => !image.IsCapture)];

        // A picture the user pasted is in memory already: nothing is read from disk for it, so the Files permission is not what lets it through. The user
        // put it there themselves, for this question.
        var pasted = images.Where(image => image.IsPasted)
            .Select(image => new ContextItem(Guid.NewGuid(), ContextItemType.Image, image.Name) { ImageData = image.Data, Source = ContextSource.UserSelected })
            .ToList();
        images = [.. images.Where(image => !image.IsPasted)];
        if (images.Count == 0)
        {
            return new AttachedImagesResult(pasted, null);
        }

        if (permissions is null)
        {
            return new AttachedImagesResult([], NotAvailableText);
        }

        if (!(await permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(true)).IsAllowed)
        {
            return new AttachedImagesResult([], FilesTurnedOffText);
        }

        var items = new List<ContextItem>(pasted);
        foreach (var image in images)
        {
            if (image.Path is not { } path)
            {
                return new AttachedImagesResult([], UnreadableText);
            }

            try
            {
                if (new FileInfo(path).Length > MaxBytes)
                {
                    return new AttachedImagesResult([], UnreadableText);
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
                items.Add(new ContextItem(Guid.NewGuid(), ContextItemType.Image, image.Name)
                {
                    FilePath = path,
                    ImageData = bytes,
                    Source = ContextSource.UserSelected,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new AttachedImagesResult([], UnreadableText);
            }
        }

        return new AttachedImagesResult(items, null);
    }
}
