using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// "Add homework to my to do": people write the app's name the way they say it, two short words, and that request is about the To Do app the user connected,
/// whose tools are then offered, and not about opening the app.
/// </summary>
public sealed class ToDoRequestTests
{
    private static InstalledIntegration ToDo(params string[] tools) =>
        Sample.Remote("microsofttodopipedream", "Microsoft To Do") with
        {
            Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = tools, RefreshedAt = DateTimeOffset.UtcNow },
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy },
        };

    [Theory]
    [InlineData("add homework to my to do, set it due today")]
    [InlineData("add buy milk to my to do")]
    [InlineData("put call the dentist on my to-do list")]
    [InlineData("add homework to my todo")]
    [InlineData("add homework to Microsoft To Do")]
    public void ARequestForMyToDoIsAboutTheToDoAppTheUserConnected(string request)
    {
        var todo = ToDo("microsoft_todo-create-task", "microsoft_todo-list-tasks", "microsoft_todo-find-task");

        Assert.Equal([todo], LexicalMcpToolSelector.Instance.SelectIntegrations(request, [todo]));
    }

    [Theory]
    [InlineData("what do I have to do today")] // "to do" as words, about the user's day.
    [InlineData("I have a lot to do")]
    public void ToDoAsOrdinaryWordsIsNot(string request)
    {
        var todo = ToDo("microsoft_todo-create-task", "microsoft_todo-list-tasks");

        Assert.Empty(LexicalMcpToolSelector.Instance.SelectIntegrations(request, [todo]));
    }
}
