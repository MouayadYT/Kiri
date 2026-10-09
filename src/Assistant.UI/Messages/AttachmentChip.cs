namespace Assistant.UI.Messages;

/// <summary>What a chip above a composer stands for.</summary>
public enum AttachmentKind
{
    /// <summary>A picture: the chip shows its thumbnail.</summary>
    Image,

    /// <summary>A document: the chip shows a file glyph.</summary>
    File,

    /// <summary>A piece of text: the chip shows a text glyph.</summary>
    Text,
}

/// <summary>
/// One chip of the row above a composer (PROJECT_SPEC §4.2): a small icon or thumbnail, the attachment's name cut to fit, and a
/// button that takes it off. It only describes what it stands for; the attachment itself is its <see cref="Source"/>, the image,
/// the document or the text, which is what the remove command is given.
/// </summary>
public sealed class AttachmentChip
{
    private AttachmentChip(AttachmentKind kind, object source, string title, string detail, ImageItem? image = null)
    {
        Kind = kind;
        Source = source;
        Title = title;
        Detail = detail;
        Image = image;
    }

    /// <summary>What the chip stands for, which decides its icon.</summary>
    public AttachmentKind Kind { get; }

    /// <summary>The <see cref="ImageItem"/>, <see cref="DocumentAttachment"/> or <see cref="TextAttachment"/> the chip stands for.</summary>
    public object Source { get; }

    /// <summary>What the chip says, such as a file's name; long ones are cut with an ellipsis where it is drawn.</summary>
    public string Title { get; }

    /// <summary>More about what is attached, shown when the pointer rests on the chip: where a file is, or how long a text is.</summary>
    public string Detail { get; }

    /// <summary>The picture of an image chip, whose thumbnail fills in once it is decoded; <see langword="null"/> for the others.</summary>
    public ImageItem? Image { get; }

    /// <summary>What the chip's remove button is called for assistive technology.</summary>
    public string RemoveName => Kind switch
    {
        AttachmentKind.File => "Remove attached file",
        AttachmentKind.Text => "Remove attached text",
        _ => "Remove attachment",
    };

    /// <summary>The chip for an attached picture.</summary>
    public static AttachmentChip For(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var detail = image.IsCapture
            ? $"Part of your screen, {image.PixelWidth:N0} × {image.PixelHeight:N0} pixels. It stays in memory, and goes with each question until you remove it."
            : image.IsPasted
                ? $"Pasted from the clipboard, {image.PixelWidth:N0} × {image.PixelHeight:N0} pixels. It stays in memory, and goes with your next question."
                : image.Path ?? image.Name;
        return new(AttachmentKind.Image, image, image.Name, detail, image);
    }

    /// <summary>The chip for an attached document.</summary>
    public static AttachmentChip For(DocumentAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(AttachmentKind.File, document, document.Name, document.Path);
    }

    /// <summary>The chip for an attached piece of text.</summary>
    public static AttachmentChip For(TextAttachment text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var detail = $"Text, {text.Text.Length:N0} characters";
        return new(AttachmentKind.Text, text, text.Name, text.HasNearbyContext ? $"{detail}. {text.NearbyNotice}." : detail);
    }
}
