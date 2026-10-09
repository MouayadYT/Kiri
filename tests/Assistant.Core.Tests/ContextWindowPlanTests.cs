using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Which context window the model is loaded with (PROJECT_SPEC §5.5, §5.6): 8,000 tokens for an ordinary conversation, 32,000 while the conversation being
/// answered carries files, and the ordinary one again afterwards; a window the user set, and a hardware preset they chose, are respected.
/// </summary>
public sealed class ContextWindowPlanTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly ContextLimitSettings Defaults = new();

    private static Message User(string text, params ContextItem[] items) =>
        new(Guid.NewGuid(), MessageRole.User, text, DateTimeOffset.UnixEpoch) { ContextItems = items };

    private static ContextItem Item(ContextItemType type) => new(Guid.NewGuid(), type, "item") { Text = "text" };

    [Fact]
    public void AnOrdinaryConversationHasEightThousandTokens_AndOneWithFilesThirtyTwoThousand()
    {
        Assert.Equal(8000, ModelFiles.DefaultContextLength);
        Assert.Equal(32000, ModelFiles.DocumentContextLength);
        Assert.Equal(8000, ContextWindowPlan.Window(pinned: null, Defaults, documents: false));
        Assert.Equal(32000, ContextWindowPlan.Window(pinned: null, Defaults, documents: true));
    }

    [Fact]
    public void TheUsersOwnLimitsAreTheTwoWindows_AndFilesNeverGetLessThanAChat()
    {
        var limits = new ContextLimitSettings { NormalContextTokens = 6000, HeavyContextTokens = 20000 };
        Assert.Equal(6000, ContextWindowPlan.Window(null, limits, documents: false));
        Assert.Equal(20000, ContextWindowPlan.Window(null, limits, documents: true));

        var backwards = new ContextLimitSettings { NormalContextTokens = 12000, HeavyContextTokens = 4000 };
        Assert.Equal(12000, ContextWindowPlan.Window(null, backwards, documents: true));
    }

    [Fact]
    public void AWindowTheUserSetIsUsedForEverything()
    {
        Assert.Equal(16384, ContextWindowPlan.Window(pinned: 16384, Defaults, documents: false));
        Assert.Equal(16384, ContextWindowPlan.Window(pinned: 16384, Defaults, documents: true));
    }

    [Fact]
    public void NoLimitLeavesTheWindowToWhatSuitsThePc()
    {
        var unlimited = new ContextLimitSettings { NormalContextTokens = 0, HeavyContextTokens = 0 };
        Assert.Equal(12288, ContextWindowPlan.Window(null, unlimited, documents: false, fallback: 12288));
        Assert.Equal(12288, ContextWindowPlan.Window(null, unlimited, documents: true, fallback: 12288));
        Assert.Equal(ModelFiles.DefaultContextLength, ContextWindowPlan.Window(null, unlimited, documents: false));
    }

    [Fact]
    public void APresetTheUserChoseHoldsBothWindows()
    {
        Assert.Equal(4096, ContextWindowPlan.Window(null, Defaults, documents: false, ceiling: 4096));
        Assert.Equal(4096, ContextWindowPlan.Window(null, Defaults, documents: true, ceiling: 4096));
        Assert.Equal(8000, ContextWindowPlan.Window(null, Defaults, documents: false, ceiling: 16384));
        Assert.Equal(16384, ContextWindowPlan.Window(null, Defaults, documents: true, ceiling: 16384));
    }

    [Fact]
    public void AFilesWindowThePcsMemoryWouldNotHoldIsMadeSmaller_ButNeverBelowTheOrdinaryOne()
    {
        // A model the user picked by file is taken to be an 8B one: plenty of room on 32 GB, not on 8 GB, and none to spare on 4 GB.
        var roomy = new HardwareInfo(32 * GiB, 16);
        var small = new HardwareInfo(8 * GiB, 8);
        var tiny = new HardwareInfo(4 * GiB, 4);

        Assert.Equal(32000, ContextWindowPlan.Window(null, Defaults, documents: true, hardware: roomy));

        var held = ContextWindowPlan.Window(null, Defaults, documents: true, hardware: small);
        Assert.InRange(held, 8000, 31999);
        Assert.True(ContextAdvisor.EstimateMemoryBytes(null, held) <= ContextAdvisor.ExceedsMemory * small.TotalMemoryBytes || held == 8000);

        Assert.Equal(8000, ContextWindowPlan.Window(null, Defaults, documents: true, hardware: tiny));

        // The ordinary window is never changed by it.
        Assert.Equal(8000, ContextWindowPlan.Window(null, Defaults, documents: false, hardware: tiny));

        // A model file whose size is known is sized by its file and not taken for an 8B one: the 3.1 GB download of the setup (a 4B model) leaves
        // room on 8 GB for a files window well above the ordinary one, as the setup's own figure for it says.
        const long fourBillionFile = 3_108_760_000;
        var fits = ContextWindowPlan.Window(null, Defaults, documents: true, hardware: small, modelFileBytes: fourBillionFile);
        Assert.InRange(fits, 16000, 31999);
        Assert.True(ContextWindowPlan.EstimateMemoryBytes(null, fourBillionFile, fits) <= ContextAdvisor.ExceedsMemory * small.TotalMemoryBytes);
        Assert.Equal(32000, ContextWindowPlan.Window(null, Defaults, documents: true, hardware: roomy, modelFileBytes: fourBillionFile));

        // The setup's figures and the Settings page's are the same sum for the same file.
        Assert.Equal(
            ContextAdvisor.EstimateMemoryBytes(fourBillionFile, ContextAdvisor.EstimateBillions(fourBillionFile), 8000),
            ContextWindowPlan.EstimateMemoryBytes(null, fourBillionFile, 8000));
    }

    [Fact]
    public void AConversationCarriesFilesWhenOneIsAttachedSearchedForOrRead()
    {
        Assert.False(ContextWindowPlan.CarriesDocuments([User("hello")]));
        Assert.False(ContextWindowPlan.CarriesDocuments([User("what is this?", Item(ContextItemType.Selection), Item(ContextItemType.Screenshot), Item(ContextItemType.Page))]));

        Assert.True(ContextWindowPlan.CarriesDocuments([User("summarize it", Item(ContextItemType.File))]));
        Assert.True(ContextWindowPlan.CarriesDocuments([User("what do my notes say?", Item(ContextItemType.SearchResults))]));
        Assert.True(ContextWindowPlan.CarriesDocuments([User("and the second one?", Item(ContextItemType.FileNotes))]));

        // The file that comes with the question being asked counts before it is in the conversation.
        Assert.True(ContextWindowPlan.CarriesDocuments([User("hello")], [Item(ContextItemType.File)]));

        // A conversation that had a file earlier is still about it.
        Assert.True(ContextWindowPlan.CarriesDocuments([User("read this", Item(ContextItemType.File)), User("thanks, and one more thing")]));
    }

    [Fact]
    public void TheModelLookingForOrReadingAFileMakesTheConversationOneAboutFiles()
    {
        var asked = User("what does my lease say about pets?");
        var called = new Message(Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UnixEpoch)
        {
            ToolCalls = [new ToolCall("c1", FileToolResults.SearchFiles, """{"query":"lease"}""")],
        };
        var calculated = new Message(Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UnixEpoch)
        {
            ToolCalls = [new ToolCall("c2", "calculate", """{"expression":"1+1"}""")],
        };

        Assert.True(ContextWindowPlan.CarriesDocuments([asked, called]));
        Assert.False(ContextWindowPlan.CarriesDocuments([asked, calculated]));
        Assert.True(ContextWindowPlan.IsFileTool(FileToolResults.ReadFileText));
        Assert.False(ContextWindowPlan.IsFileTool("search_web"));
    }
}
