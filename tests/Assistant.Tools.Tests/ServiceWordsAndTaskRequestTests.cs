using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The service a request names for a message ("on iMessage"), read from the user's own words however they were typed, so that it does not depend
/// on a small model passing it along; and a request to add a task, which is about the To Do app the user connected and not about opening it.
/// </summary>
public sealed class ServiceWordsAndTaskRequestTests
{
    [Theory]
    [InlineData("send a messaying to my brother on imesasge saying hi", "iMessage")] // As the user typed it.
    [InlineData("send a message to my brother on iMessage saying hi", "iMessage")]
    [InlineData("message Sami on watsapp that I am late", "WhatsApp")]
    [InlineData("text my sister through Signal", "Signal")]
    [InlineData("send hi to marcus on beeper", "Beeper")]
    public void TheServiceARequestNamesIsRead_AlsoWhenItIsMistyped(string request, string service) =>
        Assert.Equal(service, MessagingServiceWords.Named(request));

    [Theory]
    [InlineData("send a message to my brother saying hi")]
    [InlineData("send a message to my brother saying the signs are up")] // "signs" is not Signal.
    [InlineData("tell Sami I will slack off today on WhatsApp or Signal")] // More than one: the user is asked.
    [InlineData("")]
    [InlineData(null)]
    public void ARequestThatNamesNoServiceOrMoreThanOneNamesNone(string? request) =>
        Assert.Null(MessagingServiceWords.Named(request));

    private static InstalledIntegration ToDo() =>
        Sample.Remote("microsofttodopipedream", "Microsoft To Do") with
        {
            Capabilities = new IntegrationCapabilities
            {
                Tools = true, ToolNames = ["microsoft_todo-create-task", "microsoft_todo-list-tasks", "microsoft_todo-find-task"], RefreshedAt = DateTimeOffset.UtcNow,
            },
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy },
        };

    [Theory]
    [InlineData("add task homework due today in ms to due")] // As the user typed it: "to do" came out as "to due".
    [InlineData("add a task to buy milk")]
    [InlineData("create a new task: call the dentist tomorrow")]
    [InlineData("put homework on my tasks")]
    [InlineData("make a todo for the report")]
    public void ARequestToAddATaskIsAboutTheToDoAppTheUserConnected(string request)
    {
        var todo = ToDo();

        Assert.Equal([todo], LexicalMcpToolSelector.Instance.SelectIntegrations(request, [todo]));
    }

    [Theory]
    [InlineData("open task manager")]
    [InlineData("what is a task in an operating system")]
    [InlineData("add 4 and 5")]
    [InlineData("make a new folder on the desktop")]
    public void ARequestThatOnlyMentionsATaskOrOnlyAddsSomethingIsNot(string request)
    {
        var todo = ToDo();

        Assert.Empty(LexicalMcpToolSelector.Instance.SelectIntegrations(request, [todo]));
    }
}