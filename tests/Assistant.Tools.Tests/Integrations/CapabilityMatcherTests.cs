using Assistant.Tools.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 105: whether a tool does what a capability asks, by the words of its name and description.</summary>
public sealed class CapabilityMatcherTests
{
    private static readonly IntegrationCapability CreateTask = new(CapabilityAction.Create, "task");

    private static IReadOnlyList<string> Match(IntegrationCapability capability, params string[] names) =>
        CapabilityMatcher.Match(capability, [.. names.Select(name => new ToolFacts(name))]);

    [Theory]
    [InlineData("create_task")]
    [InlineData("add_task")]
    [InlineData("add-tasks")]
    [InlineData("createTask")]
    [InlineData("todoist_create_task")]
    [InlineData("tasks.insert")]
    [InlineData("tasks_create")]
    [InlineData("insertTodo")]
    [InlineData("new_todo_item")]
    [InlineData("quick_add_reminder")]
    public void AToolNamedForTheActionAndTheThingMatches(string name)
    {
        Assert.Equal([name], Match(CreateTask, name));
    }

    [Theory]
    [InlineData("list_tasks")]
    [InlineData("delete_task")]
    [InlineData("complete_task")]
    [InlineData("create_event")]
    [InlineData("send_message")]
    [InlineData("get_user")]
    [InlineData("ping")]
    public void AToolForAnotherActionOrAnotherThingDoesNot(string name)
    {
        Assert.Empty(Match(CreateTask, name));
    }

    [Fact]
    public void TheDescriptionCanSupplyTheOtherHalf()
    {
        var quickAdd = new ToolFacts("quick_add", null, "Add a task using natural language");
        var tasks = new ToolFacts("tasks", null, "Create, list or complete tasks");

        var matched = CapabilityMatcher.Match(CreateTask, [quickAdd, tasks]);

        Assert.Equal(["quick_add", "tasks"], matched);
    }

    [Fact]
    public void ADescriptionAloneDoesNotMakeATool()
    {
        var unrelated = new ToolFacts("ping", null, "Create a task if you like, or add one.");
        Assert.Empty(CapabilityMatcher.Match(CreateTask, [unrelated]));
    }

    [Fact]
    public void AToolThatMatchesOnItsNameComesBeforeOneThatMatchesOnItsDescription()
    {
        var weak = new ToolFacts("quick_add", null, "Add a task using natural language");
        var strong = new ToolFacts("create_task");
        Assert.Equal(["create_task", "quick_add"], CapabilityMatcher.Match(CreateTask, [weak, strong]));
    }

    [Fact]
    public void AToolNamedForTheThingItselfComesBeforeOneNamedForSomethingAroundIt()
    {
        // "create_task" makes a task; "create_task_list" makes the list a task goes in.
        Assert.Equal(["create_task", "add_task_list"], Match(CreateTask, "add_task_list", "create_task"));
        Assert.Equal(["create_task", "create_task_list"], Match(CreateTask, "create_task", "create_task_list"));
        Assert.Equal(["createTask", "create_task_list"], Match(CreateTask, "create_task_list", "createTask"));
    }

    [Fact]
    public void AToolForManagingTheThingDoesEveryAction()
    {
        Assert.Equal(["manage_tasks"], Match(CreateTask, "manage_tasks"));
        Assert.Equal(["manage_tasks"], Match(new IntegrationCapability(CapabilityAction.Update, "task"), "manage_tasks"));
    }

    [Theory]
    [InlineData(CapabilityAction.Read, "task", "get_tasks", true)]
    [InlineData(CapabilityAction.Read, "task", "list_tasks", true)]
    [InlineData(CapabilityAction.Read, "event", "list_events", true)]
    [InlineData(CapabilityAction.Search, "note", "search_notes", true)]
    [InlineData(CapabilityAction.Search, "note", "find-pages", true)]
    [InlineData(CapabilityAction.Update, "task", "update_task", true)]
    [InlineData(CapabilityAction.Complete, "task", "complete_task", true)]
    [InlineData(CapabilityAction.Send, "message", "post_message", true)]
    [InlineData(CapabilityAction.Send, "message", "chat_postMessage", true)]
    [InlineData(CapabilityAction.Create, "issue", "create_issue", true)]
    [InlineData(CapabilityAction.Create, "issue", "open_ticket", false)]
    [InlineData(CapabilityAction.Create, "pull request", "create_pull_request", true)]
    [InlineData(CapabilityAction.Create, "event", "create_calendar_event", true)]
    [InlineData(CapabilityAction.Create, "note", "notion-create-pages", true)]
    public void EachActionHasItsOwnWords(CapabilityAction action, string obj, string tool, bool expected)
    {
        Assert.Equal(expected, Match(new IntegrationCapability(action, obj), tool).Count == 1);
    }

    [Fact]
    public void ACapabilityWithNoThingMatchesOnTheActionAlone()
    {
        Assert.Equal(["create_anything"], Match(new IntegrationCapability(CapabilityAction.Create, null), "create_anything", "list_stuff"));
    }

    [Fact]
    public void EvidenceFromTheToolsOutranksEvidenceFromTheDescription()
    {
        Assert.Equal(CapabilityEvidence.ToolListed, CapabilityMatcher.EvidenceOf(CreateTask, ["create_task"], "A server"));
        Assert.Equal(CapabilityEvidence.Described, CapabilityMatcher.EvidenceOf(CreateTask, [], "Lets an assistant create a task in the app"));
        Assert.Equal(CapabilityEvidence.AppOnly, CapabilityMatcher.EvidenceOf(CreateTask, [], "A server for the app"));
        Assert.Equal(CapabilityEvidence.AppOnly, CapabilityMatcher.EvidenceOf(CreateTask, [], null));
    }

    [Fact]
    public void AReadmeMentionsACapabilityWhenOneSentenceSpeaksOfTheActionAndTheThing()
    {
        Assert.True(CapabilityMatcher.Mentions(CreateTask, "# Server" + (char)10 + "You can create tasks and projects."));
        Assert.False(CapabilityMatcher.Mentions(CreateTask, "Create things. Then look at your tasks."));
        Assert.False(CapabilityMatcher.Mentions(CreateTask, null));
    }

    [Fact]
    public void WordsAreTakenApartAndPluralsFolded()
    {
        var words = CapabilityMatcher.Stems("createTask_list.items-done");
        Assert.Contains("create", words);
        Assert.Contains("task", words);
        Assert.Contains("list", words);
        Assert.Contains("item", words);
        Assert.Contains("done", words);
    }
}
