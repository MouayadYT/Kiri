namespace Assistant.Core.Imaging;

/// <summary>What a picture shows, which decides how it is made ready for a vision model (<see cref="Contracts.IImagePreprocessor"/>).</summary>
public enum ImageContent
{
    /// <summary>A photograph or any other picture: scaled down to the limits and no more.</summary>
    Picture = 0,

    /// <summary>
    /// A part of the user's screen (PROJECT_SPEC §4.6, §5.5): small text and thin lines in it must stay readable, so empty margins
    /// are cut off before the picture is scaled, a small one is enlarged until its text has enough pixels to read, and a scaled one is
    /// sharpened a little.
    /// </summary>
    Screenshot = 1,
}
