using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The files a conversation has made known to the model, the ids it names them by, and what the file tools say of them.</summary>
public sealed class ConversationFilesTests
{
    private static readonly Guid Chat = Guid.NewGuid();

    private static SearchResultItem File(string path, string? extension = null) =>
        new(SearchResultItemType.File, System.IO.Path.GetFileName(path), path)
        {
            Extension = extension ?? System.IO.Path.GetExtension(path),
            ModifiedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        };

    private static readonly SearchResultItem Four = File(@"C:\Users\me\Downloads\Milestone Four Guidelines and Rubric.mhtml");
    private static readonly SearchResultItem Three = File(@"C:\Users\me\Downloads\Milestone Three Guidelines and Rubric.mhtml");
    private static readonly SearchResultItem Draft = File(@"C:\Users\me\Documents\HIS-332_Milestone_Four_Rough_Draft.docx");

    [Fact]
    public void FilesGetIdsInTheOrderTheyAreOffered_AndAFileOfferedAgainKeepsItsId()
    {
        var files = new ConversationFiles();

        var first = files.Offer(Chat, [Four, Three]);
        var again = files.Offer(Chat, [Three, Draft]);

        Assert.Equal(["f1", "f2"], first.Select(file => file.Id));
        Assert.Equal(["f2", "f3"], again.Select(file => file.Id));
        Assert.Equal(("Milestone Three Guidelines and Rubric.mhtml", "Downloads"), (first[1].Name, first[1].Folder));
        Assert.True(files.Has(Chat));
        Assert.False(files.Has(Guid.NewGuid()));
    }

    [Fact]
    public void AFileIsFoundByItsId_ItsName_AFragmentOnlyOneFileHas_OrItsPath_AndNotByAnythingElse()
    {
        var files = new ConversationFiles();
        files.Offer(Chat, [Four, Three, Draft]);

        Assert.Equal("f2", files.Find(Chat, "f2")!.Id);
        Assert.Equal("f2", files.Find(Chat, " [F2] ")!.Id);
        Assert.Equal("f2", files.Find(Chat, "Milestone Three Guidelines and Rubric.mhtml")!.Id);
        Assert.Equal("f2", files.Find(Chat, "Milestone Three Guidelines and Rubric")!.Id);
        Assert.Equal("f2", files.Find(Chat, "milestone three")!.Id);
        Assert.Equal("f3", files.Find(Chat, @"C:\Users\me\Documents\HIS-332_Milestone_Four_Rough_Draft.docx")!.Id);

        // "milestone" is in all three names, and nothing else is known: no guess.
        Assert.Null(files.Find(Chat, "milestone"));
        Assert.Null(files.Find(Chat, "f9"));
        Assert.Null(files.Find(Chat, ""));
        Assert.Null(files.Find(Chat, @"C:\Windows\system32\config\SAM"));
        Assert.Null(files.Find(Guid.NewGuid(), "f1"));
    }

    [Fact]
    public void ConversationsKeepTheirOwnFiles_AndForgetThemWhenAsked()
    {
        var files = new ConversationFiles();
        var other = Guid.NewGuid();
        files.Offer(Chat, [Four]);
        files.Offer(other, [Three]);

        Assert.Equal("Milestone Four Guidelines and Rubric.mhtml", files.Find(Chat, "f1")!.Name);
        Assert.Equal("Milestone Three Guidelines and Rubric.mhtml", files.Find(other, "f1")!.Name);
        Assert.Equal(["f1"], files.Get(Chat, ["f1", "f7"]).Select(file => file.Id));

        files.Forget(Chat);
        Assert.False(files.Has(Chat));
        Assert.True(files.Has(other));
    }

    [Fact]
    public void ManyFilesAndManyConversationsAreBounded_AndAnIdIsNeverGivenAgain()
    {
        var files = new ConversationFiles();
        var offered = Enumerable.Range(1, ConversationFiles.MaxFiles + 5).Select(i => File(@$"C:\Docs\file{i}.txt")).ToArray();

        files.Offer(Chat, offered);

        Assert.Null(files.Find(Chat, "f1"));
        Assert.NotNull(files.Find(Chat, "f" + (ConversationFiles.MaxFiles + 5)));
        Assert.Equal("f" + (ConversationFiles.MaxFiles + 6), files.Offer(Chat, [File(@"C:\Docs\new.txt")])[0].Id);

        var first = Guid.NewGuid();
        files.Offer(first, [Four]);
        for (var i = 0; i < ConversationFiles.MaxConversations; i++)
        {
            files.Offer(Guid.NewGuid(), [Three]);
        }

        Assert.False(files.Has(first));
    }

    [Fact]
    public void WhatASearchFoundIsToldToTheModelByIdAndName_NeverByPath_AndReadBackByTheApp()
    {
        var files = new ConversationFiles();
        var known = files.Offer(Chat, [Four, Three, Draft]);

        var json = FileToolResults.Found(known, "No name has the words exactly; these are the closest.", images: false);

        Assert.Contains("\"id\":\"f1\"", json, StringComparison.Ordinal);
        Assert.Contains("Milestone Three Guidelines and Rubric.mhtml", json, StringComparison.Ordinal);
        Assert.Contains("\"folder\":\"Downloads\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"mhtml\"", json, StringComparison.Ordinal);
        Assert.Contains("2026-09-21", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\\Users", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", json, StringComparison.Ordinal);

        var result = new ToolResult("c", FileToolResults.SearchFiles, ToolResultStatus.Succeeded, json);
        Assert.True(FileToolResults.TryReadFound(result, out var ids, out var images));
        Assert.Equal(["f1", "f2", "f3"], ids);
        Assert.False(images);

        var pictures = FileToolResults.Found(known, null, images: true);
        Assert.True(FileToolResults.TryReadFound(result with { OutputJson = pictures }, out _, out var asked));
        Assert.True(asked);
    }

    [Fact]
    public void ASearchThatFoundNothingOrFailed_IsNotAListToShow()
    {
        var none = new ToolResult("c", FileToolResults.SearchFiles, ToolResultStatus.Succeeded, FileToolResults.Found([], "Nothing was found.", false));
        var failed = new ToolResult("c", FileToolResults.SearchFiles, ToolResultStatus.Failed, FileToolResults.Error("no"));
        var other = new ToolResult("c", "other", ToolResultStatus.Succeeded, FileToolResults.Found(new ConversationFiles().Offer(Chat, [Four]), null, false));
        var garbled = new ToolResult("c", FileToolResults.SearchFiles, ToolResultStatus.Succeeded, "not json");

        Assert.False(FileToolResults.TryReadFound(none, out _, out _));
        Assert.False(FileToolResults.TryReadFound(failed, out _, out _));
        Assert.False(FileToolResults.TryReadFound(other, out _, out _));
        Assert.False(FileToolResults.TryReadFound(garbled, out _, out _));
    }

    [Fact]
    public void AFileThatWasReadCarriesItsNoticeBackToTheApp_AndAnErrorNone()
    {
        var read = new ToolResult("c", FileToolResults.ReadFileText, ToolResultStatus.Succeeded,
            FileToolResults.Read("f2", "Three.mhtml", "<untrusted_context>x</untrusted_context>", "5 of 6 parts", "“Three.mhtml” is long, so only 5 parts were read."));
        var error = new ToolResult("c", FileToolResults.ReadFileText, ToolResultStatus.Failed, FileToolResults.Error("gone"));

        Assert.Equal("“Three.mhtml” is long, so only 5 parts were read.", FileToolResults.ReadNotice(read));
        Assert.Null(FileToolResults.ReadNotice(error));
    }

    [Fact]
    public void ManyFilesFound_ArePutToTheModelUpToALimit_AndTheRestIsSaidInWords()
    {
        var files = new ConversationFiles();
        var known = files.Offer(Chat, Enumerable.Range(1, 14).Select(i => File(@$"C:\Docs\file{i}.txt")));

        var json = FileToolResults.Found(known, null, false);

        Assert.Contains("\"found\":14", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"f10\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"f11\"", json, StringComparison.Ordinal);
        Assert.Contains("4 more files", json, StringComparison.Ordinal);
    }
}
