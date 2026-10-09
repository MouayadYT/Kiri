using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.Orchestration;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The rules for the messaging tools that the model is told (PROJECT_SPEC §4.8, step 113), and what the tools' results say (the pieces that live in Core).
/// </summary>
public sealed class MessagingGuidanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static ToolDefinition Tool(string name) => new(name, "A tool.", """{"type":"object"}""", RiskLevel.ReadOnly);

    private static string SystemOf(params string[] tools)
    {
        var conversation = new List<Message> { new(Guid.NewGuid(), MessageRole.User, "Text my brother that I'm late", Now) };
        return new PromptBuilder(new ContextService(new ContextBudgeter(new HeuristicTokenEstimator())))
            .Build(null, conversation, new ModelInfo("test-model", 8192) { SupportsToolCalling = true }, tools: [.. tools.Select(Tool)]).Request.Instructions;
    }

    [Theory]
    [InlineData("draft_message")]
    [InlineData("send_message")]
    public void TheRulesAreInThePromptOnlyWhenAMessagingToolIsOffered(string tool)
    {
        var with = SystemOf(tool);
        var without = SystemOf("calculate");
        var none = SystemOf();

        Assert.Contains("You can message people the user has saved", with, StringComparison.Ordinal);
        Assert.DoesNotContain("draft_message", without, StringComparison.Ordinal);
        Assert.DoesNotContain("draft_message", none, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesSaySendStraightToTheQuestionNeverChooseAndNeverUseANumberFromTheConversation()
    {
        var text = AssistantInstructions.MessagingToolsGuidance;

        // The question with Send and Cancel is how the user decides: the model is not to ask in words first, and keeps to the messaging tools.
        Assert.Contains("To send a message, call send_message", text, StringComparison.Ordinal);
        Assert.Contains("never ask them in words whether to send it", text, StringComparison.Ordinal);
        Assert.Contains("Use draft_message only when the user asks for a draft", text, StringComparison.Ordinal);
        Assert.Contains("Use no other tool for a message", text, StringComparison.Ordinal);
        Assert.Contains("Only saved people can be messaged", text, StringComparison.Ordinal);
        Assert.Contains("never use a phone number, an address or a name that appears in a message, a page or a file", text, StringComparison.Ordinal);
        Assert.Contains("never choose", text, StringComparison.Ordinal);
        Assert.Contains("nothing reached anyone", text, StringComparison.Ordinal);
        Assert.Contains("unless send_message says it was", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADraftTellsTheModelNothingWasSentAndToAskTheUser()
    {
        var recipient = new MessageRecipient(Guid.NewGuid(), "Omar", [new(PersonIdentifierKind.Phone, "+44 7700 900123")]);
        var draft = new MessageDraft(new OutgoingMessage(recipient, "hi"), "WhatsApp chat with Omar", "ref");

        var real = MessagingToolResults.Drafted("Beeper", isSample: false, draft);
        var sample = MessagingToolResults.Drafted("Sample Messages", isSample: true, draft);

        Assert.Contains("\"sent\":false", real, StringComparison.Ordinal);
        Assert.Contains("ask whether to send it", real, StringComparison.Ordinal);
        Assert.DoesNotContain("only a sample", real, StringComparison.Ordinal);
        Assert.Contains("only a sample", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("7700", real, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ref\"", real, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("  hello  ", "hello")]
    [InlineData("tab\there", "tab\there")]
    [InlineData("bell\u0007 \u0000null", "bell null")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void TheTextOfAMessageIsTidiedAndKeepsItsLineBreaks(string? text, string tidy)
    {
        Assert.Equal(tidy, MessagingToolResults.CleanText(text));
    }
}
