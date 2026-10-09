using Assistant.Tools.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 105: which app a request is about and what it wants done there, read by fixed rules.</summary>
public sealed class IntegrationRequestReaderTests
{
    private static IntegrationNeed? Read(string request, params string[] installed) => IntegrationRequestReader.Instance.Read(request, installed);

    [Fact]
    public void TheExampleInTheStepIsMicrosoftToDoAndCreateTask()
    {
        var need = Read("Add 'buy milk' to Microsoft To Do");

        Assert.NotNull(need);
        Assert.Equal("Microsoft To Do", need.AppName);
        Assert.Equal("microsofttodo", need.AppKey);
        Assert.Equal(CapabilityAction.Create, need.Capability.Action);
        Assert.Equal("task", need.Capability.Object);
        Assert.Equal("create task", need.Capability.Phrase);
        Assert.Equal(IntegrationNeedSource.Catalog, need.Source);
    }

    [Theory]
    [InlineData("Add buy milk to Microsoft To Do.", "microsofttodo", CapabilityAction.Create, "task")]
    [InlineData("add milk to ms todo", "microsofttodo", CapabilityAction.Create, "task")]
    [InlineData("Add milk to my Todoist", "todoist", CapabilityAction.Create, "task")]
    [InlineData("Add milk to my Todoist list", "todoist", CapabilityAction.Create, "task")]
    [InlineData("Can you add a task to Todoist: call Anna?", "todoist", CapabilityAction.Create, "task")]
    [InlineData("Could you please put milk on my Todoist", "todoist", CapabilityAction.Create, "task")]
    [InlineData("I want to add a reminder in Todoist", "todoist", CapabilityAction.Create, "task")]
    [InlineData("Remind me to buy milk in Todoist", "todoist", CapabilityAction.Create, "task")]
    [InlineData("In Todoist, add milk", "todoist", CapabilityAction.Create, "task")]
    [InlineData("Please create a new page in Notion called Ideas", "notion", CapabilityAction.Create, "page")]
    [InlineData("Create a Notion page called Ideas", "notion", CapabilityAction.Create, "page")]
    [InlineData("Show my tasks in Microsoft To Do", "microsofttodo", CapabilityAction.Read, "task")]
    [InlineData("What's on my Google Calendar tomorrow?", "googlecalendar", CapabilityAction.Read, "event")]
    [InlineData("Schedule a meeting in Google Calendar for Friday", "googlecalendar", CapabilityAction.Create, "event")]
    [InlineData("Send a message to the team on Slack", "slack", CapabilityAction.Send, "message")]
    [InlineData("Create an issue in GitHub: login fails", "github", CapabilityAction.Create, "issue")]
    [InlineData("Mark the milk task as done in Todoist", "todoist", CapabilityAction.Complete, "task")]
    [InlineData("Find my notes about the budget in Notion", "notion", CapabilityAction.Search, "note")]
    [InlineData("Add a song to my Spotify queue", "spotify", CapabilityAction.Create, "track")]
    [InlineData("Email Anna from Outlook", "outlook", CapabilityAction.Send, "message")]
    [InlineData("Create a Linear issue for the crash", "linear", CapabilityAction.Create, "issue")]
    [InlineData("Add this to Linear", "linear", CapabilityAction.Create, "issue")]
    [InlineData("Close the issue in GitHub", "github", CapabilityAction.Complete, "issue")]
    [InlineData("send a message to my borther on beeper", "beeper", CapabilityAction.Send, "message")]
    [InlineData("Send a message to my brother on Beeper", "beeper", CapabilityAction.Send, "message")]
    [InlineData("message my mom on Beeper", "beeper", CapabilityAction.Send, "message")]
    [InlineData("text my brother on beeper saying I am late", "beeper", CapabilityAction.Send, "message")]
    [InlineData("Tell my brother I'm on my way using Beeper", "beeper", CapabilityAction.Send, "message")]
    [InlineData("on beeper, send omar hello", "beeper", CapabilityAction.Send, "message")]
    [InlineData("Send my brother a message through Beeper", "beeper", CapabilityAction.Send, "message")]
    public void ARequestForAKnownAppIsReadAsTheAppAndTheCapability(string request, string key, CapabilityAction action, string obj)
    {
        var need = Read(request);

        Assert.NotNull(need);
        Assert.Equal(key, need.AppKey);
        Assert.Equal(action, need.Capability.Action);
        Assert.Equal(obj, need.Capability.Object);
    }

    [Fact]
    public void AskingToDeleteIsReadAsADeleteSoThatItCanBeRefused()
    {
        var need = Read("Delete the task in Todoist");

        Assert.NotNull(need);
        Assert.Equal(CapabilityAction.Delete, need.Capability.Action);
    }

    [Theory]
    [InlineData("How do I add a task in Todoist?")]
    [InlineData("What is Todoist?")]
    [InlineData("Why can't I add tasks to Todoist")]
    [InlineData("Can I add tasks to Todoist?")]
    [InlineData("Is it possible to add a task to Todoist")]
    [InlineData("Explain how to create a page in Notion")]
    [InlineData("Open Spotify")]
    [InlineData("Launch Todoist")]
    [InlineData("Close Spotify")]
    [InlineData("Install Notion")]
    [InlineData("Add milk to my shopping list")]
    [InlineData("What is 2 plus 2")]
    [InlineData("Todoist")]
    [InlineData("I like Todoist")]
    [InlineData("Summarize this document")]
    [InlineData("Send the file to Anna")]
    [InlineData("")]
    [InlineData("   ")]
    public void ARequestThatIsNotAnActionInAnExternalAppReadsAsNothing(string request)
    {
        Assert.Null(Read(request));
    }

    [Theory]
    [InlineData("Add Slack to my startup apps")]
    [InlineData("Update Todoist")]
    [InlineData("Delete Notion from this PC")]
    [InlineData("Add the Slack app to my taskbar")]
    public void AnAppThatIsTheThingActedOnIsNotTheAppTheRequestIsAbout(string request)
    {
        Assert.Null(Read(request));
    }

    [Theory]
    [InlineData("Add the tasks to the linear algebra course")]
    [InlineData("Add a note that the linear model is wrong")]
    [InlineData("Send the zoom level to Anna")]
    [InlineData("Tell Anna the teams meet at noon")]
    public void AnAppNameThatIsAlsoAnOrdinaryWordNeedsSomethingAroundItToCount(string request)
    {
        Assert.Null(Read(request));
    }

    [Fact]
    public void AnInstalledIntegrationCountsAsAnAppByItsName()
    {
        var need = Read("Add milk to Fooble Tasks", "Fooble Tasks");

        Assert.NotNull(need);
        Assert.Equal(IntegrationNeedSource.Installed, need.Source);
        Assert.Equal(AppIdentity.KeyOf("Fooble Tasks"), need.AppKey);
        Assert.Equal(CapabilityAction.Create, need.Capability.Action);
        Assert.Null(need.Capability.Object);
    }

    [Fact]
    public void AKnownAppThatIsInstalledIsStillTheKnownApp()
    {
        var need = Read("Add milk to Todoist", "Todoist MCP");

        Assert.NotNull(need);
        Assert.Equal("todoist", need.AppKey);
        Assert.Equal(IntegrationNeedSource.Installed, need.Source);
    }

    [Fact]
    public void AnAppTheRequestCallsAnAppButTheAssistantDoesNotKnowIsReadAsStated()
    {
        var need = Read("Add milk to the Fooble app");

        Assert.NotNull(need);
        Assert.Equal("Fooble", need.AppName);
        Assert.Equal(IntegrationNeedSource.Stated, need.Source);
        Assert.Equal(CapabilityAction.Create, need.Capability.Action);
    }

    [Theory]
    [InlineData("Add milk to the Windows app")]
    [InlineData("Add milk to my account")]
    [InlineData("add milk to the fooble app")]
    public void AStatedAppNeedsAProperNameThatIsNotAnOrdinaryWord(string request)
    {
        Assert.Null(Read(request));
    }

    [Fact]
    public void AnUnknownNameWithoutTheWordAppIsNeverGuessed()
    {
        Assert.Null(Read("Add milk to Fooble"));
        Assert.Null(Read("Send this to John"));
    }

    [Fact]
    public void TheWordsOfTheRequestBeyondTheCapabilityAreNotKeptInTheNeed()
    {
        var need = Read("Add 'call my dentist about the secret invoice' to Todoist");

        Assert.NotNull(need);
        var kept = need.ToString();
        Assert.DoesNotContain("dentist", kept, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoice", kept, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", kept, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVeryLongRequestIsAPastedTextAndNotARequestForAnApp()
    {
        Assert.Null(Read("Add milk to Todoist " + new string('x', IntegrationRequestReader.MaxRequestLength)));
    }

    [Fact]
    public void TheSameRequestAlwaysReadsTheSame()
    {
        var first = Read("Add milk to Microsoft To Do");
        var second = Read("Add milk to Microsoft To Do");
        Assert.Equal(first, second);
    }

    [Fact]
    public void EveryKnownAppHasAUniqueKeyAndIsFoundByItsOwnName()
    {
        var keys = KnownApps.All.Select(app => app.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var app in KnownApps.All)
        {
            Assert.Same(app, KnownApps.Find(app.Name));
            Assert.Equal(app.Key, AppIdentity.KeyOf(app.Name));
            foreach (var alias in app.Aliases)
            {
                Assert.Equal(app.Key, AppIdentity.KeyOf(alias));
            }
        }
    }

    [Fact]
    public void AppsThatKeepFilesAreNotKnownApps()
    {
        Assert.Null(KnownApps.Find("OneDrive"));
        Assert.Null(KnownApps.Find("Dropbox"));
        Assert.Null(Read("Find the report in OneDrive"));
    }

    [Theory]
    [InlineData("Microsoft To Do", "microsofttodo")]
    [InlineData("microsoft todo", "microsofttodo")]
    [InlineData("MS To-Do", "microsofttodo")]
    [InlineData("Microsoft To Do MCP Server", "microsofttodo")]
    [InlineData("Todoist MCP", "todoist")]
    [InlineData("Fooble Tasks", "foobletasks")]
    public void TheKeyOfAnAppIsTheSameForEveryWayOfWritingItsName(string name, string key)
    {
        Assert.Equal(key, AppIdentity.KeyOf(name));
    }

    [Theory]
    [InlineData("mcp-microsoft-todo-server", "microsofttodo", true)]
    [InlineData("jordanburke/microsoft-todo-mcp-server", "microsofttodo", true)]
    [InlineData("todoist-mcp", "todoist", true)]
    [InlineData("Doist/todoist-mcp", "todoist", true)]
    [InlineData("todo-list-mcp", "microsofttodo", false)]
    [InlineData("A server for Jira tickets", "jira", true)]
    [InlineData("digit-tools", "git", false)]
    [InlineData("", "todoist", false)]
    public void ATextIsAboutAnAppWhenItNamesIt(string text, string key, bool expected)
    {
        Assert.Equal(expected, AppIdentity.IsAbout(text, key));
    }
}
