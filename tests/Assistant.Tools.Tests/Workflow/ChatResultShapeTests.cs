using System.Text.Json;
using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>The picture of an app's answer that goes in the log when no chat could be read from it: how it is laid out and what its fields are called, never what it says.</summary>
public sealed class ChatResultShapeTests
{
    [Fact]
    public void JsonIsDrawnAsItsKeysAndTheKindsOfItsValues()
    {
        var picture = ChatResultShape.Describe(Sample.Text(
            """{"items":[{"id":"!secret:beeper.local","title":"Savannah","type":"single","network":"WhatsApp","unreadCount":3,"isMuted":false}],"hasMore":false}"""));

        Assert.Contains("""items:[{id:s20,title:s8,type:"single",network:"WhatsApp",unreadCount:0,isMuted:false}]×1""", picture, StringComparison.Ordinal);
        Assert.Contains("hasMore:false", picture, StringComparison.Ordinal);
        Assert.DoesNotContain("Savannah", picture, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", picture, StringComparison.Ordinal);
    }

    [Fact]
    public void WordsKeepTheirLayoutAndTheNamesOfFieldsAndNothingElse()
    {
        var picture = ChatResultShape.Describe(
            Sample.Text("Found 1 chat:\n\n## Savannah Al-Secret\n- chatID: !abc123:beeper.local\n- type: single\nCall +1 555 0100 for Zorblax."));

        Assert.Contains(@"Found 9 chat:\n\n## Xxxxxxxx Xx-Xxxxxx\n- chatID: !xxx999:beeper.local\n- type: single\nXxxx +9 999 9999 for Xxxxxxx.", picture, StringComparison.Ordinal);
        foreach (var privateWord in new[] { "Savannah", "Secret", "abc123", "555", "Zorblax" })
        {
            Assert.DoesNotContain(privateWord, picture, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AKeyThatIsAnIdOrANameIsNotWritten()
    {
        var picture = ChatResultShape.Describe(Sample.Text("""{"!abc:beeper.local":{"title":"Savannah"}}"""));

        Assert.DoesNotContain("abc", picture, StringComparison.Ordinal);
        Assert.Contains("~:{title:s8}", picture, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredContentAndSeveralBlocksAreDrawnToo()
    {
        using var document = JsonDocument.Parse("""{"chats":[],"count":0}""");
        var result = new McpToolResult(
            false,
            [new McpContentBlock(McpContentKind.Text, "Nothing", null, null, null), new McpContentBlock(McpContentKind.Image, null, "image/png", null, null)],
            document.RootElement.Clone());

        var picture = ChatResultShape.Describe(result);

        Assert.Contains("structured={chats:[]×0,count:0}", picture, StringComparison.Ordinal);
        Assert.Contains("block1=Text(7 chars)=Xxxxxxx", picture, StringComparison.Ordinal);
        Assert.Contains("block2=Image", picture, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePictureIsShort()
    {
        var picture = ChatResultShape.Describe(Sample.Text(string.Join(' ', Enumerable.Repeat("word", 2000))));

        Assert.True(picture.Length <= 910, picture.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ThereIsNothingToDrawInANullResult()
    {
        Assert.Throws<ArgumentNullException>(() => ChatResultShape.Describe(null!));
    }
}
