using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>What the registry catalog says of file types, for the few that every Windows registers the same way.</summary>
public sealed class ContentTypeCatalogTests
{
    private readonly RegistryContentTypeCatalog _catalog = new();

    [Fact]
    public void PlainTextHasAFilterThatReadsItsText()
    {
        Assert.Equal(ContentTypeSupport.HasContentFilter, _catalog.Lookup(".txt"));
        Assert.Equal(ContentTypeSupport.HasContentFilter, _catalog.Lookup(".TXT"));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".gif")]
    [InlineData(".mp3")]
    [InlineData(".wav")]
    [InlineData(".mp4")]
    public void PicturesSoundsAndVideosHavePropertiesAndNoText(string extension)
    {
        Assert.Equal(ContentTypeSupport.NoTextInMedia, _catalog.Lookup(extension));
    }

    [Fact]
    public void ATypeNobodyRegisteredHasNoFilter()
    {
        Assert.Equal(ContentTypeSupport.NoContentFilter, _catalog.Lookup(".zzqqxx-9187"));
        Assert.Equal(ContentTypeSupport.NoContentFilter, _catalog.Lookup(".zzqqxx"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("txt")]
    [InlineData(".a\\b")]
    [InlineData(".a/b")]
    [InlineData(".a\0b")]
    public void SomethingThatIsNotAnExtensionIsUnknownAndNeverThrows(string extension)
    {
        Assert.Equal(ContentTypeSupport.Unknown, _catalog.Lookup(extension));
    }
}
