using Assistant.Core.Clock;
using Assistant.Windows.Clock;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Which of the Clock app's timers "stop the timer" means (PROJECT_SPEC §4.8). The Clock app keeps its own timers beside the ones the Assistant adds:
/// the ones it ships with, and whatever the user left paused. The Assistant acts on the one that can be meant, on its own before anyone else's, and
/// never on one that the action cannot be done to.
/// </summary>
public sealed class TimerChoiceTests
{
    private static readonly ListedTimer OneMinute = new("1 min", 0, Started: false, Running: false);
    private static readonly ListedTimer LeftPaused = new("Timer (1)", 0, Started: true, Running: false);
    private static readonly ListedTimer Tea = new("Tea", 0, Started: true, Running: true);
    private static readonly ListedTimer Eggs = new("Eggs", 0, Started: true, Running: true);

    private static ListedTimer? Choose(TimerAction action, string? wanted, string[] added, params ListedTimer[] timers) =>
        TimerChoice.Choose(timers, action, wanted, added, out _);

    [Fact]
    public void StopMeansTheTimerThatIsCounting_NotOneThatWasLeftPaused_AndNeverOneAtRest()
    {
        Assert.Same(Tea, Choose(TimerAction.Stop, null, [], OneMinute, Tea, LeftPaused));

        // With nothing counting, the one that was left paused is the one there is to stop.
        Assert.Same(LeftPaused, Choose(TimerAction.Stop, null, [], OneMinute, LeftPaused));
        Assert.Null(Choose(TimerAction.Stop, null, [], OneMinute));
    }

    [Fact]
    public void OfSeveralThatCount_ItIsTheOneTheAssistantStartedLast_AndElseTheLastOnTheList()
    {
        Assert.Same(Tea, Choose(TimerAction.Stop, null, ["Tea"], Tea, Eggs));
        Assert.Same(Tea, Choose(TimerAction.Stop, null, ["Eggs", "Tea"], Tea, Eggs));
        Assert.Same(Eggs, Choose(TimerAction.Stop, null, ["Tea", "Eggs"], Tea, Eggs));
        Assert.Same(Eggs, Choose(TimerAction.Stop, null, [], Tea, Eggs));
    }

    [Fact]
    public void PauseMeansACountingTimer_AndCarryingOnMeansAPausedOne()
    {
        Assert.Same(Tea, Choose(TimerAction.Pause, null, [], LeftPaused, Tea));
        Assert.Null(Choose(TimerAction.Pause, null, [], LeftPaused, OneMinute));
        Assert.Same(LeftPaused, Choose(TimerAction.Resume, null, [], LeftPaused, Tea, OneMinute));
        Assert.Null(Choose(TimerAction.Resume, null, [], Tea, OneMinute));
    }

    [Fact]
    public void ATimerIsFoundByItsName_InAnyCase_OrByPartOfIt()
    {
        Assert.Same(Eggs, Choose(TimerAction.Stop, "eggs", [], Tea, Eggs));
        Assert.Same(LeftPaused, Choose(TimerAction.Resume, "timer", [], OneMinute, LeftPaused));

        Assert.Null(TimerChoice.Choose([Tea, Eggs], TimerAction.Stop, "Bread", [], out var named));
        Assert.False(named);

        // A timer of that name that is at rest is there, with nothing to stop: which is said differently from there being none.
        Assert.Null(TimerChoice.Choose([OneMinute], TimerAction.Stop, "1 min", [], out named));
        Assert.True(named);
    }

    [Fact]
    public void ATimerAtRestCanStillBeStartedAgainByName_AndOneTheAssistantAddedCanStillBeTakenOffTheList()
    {
        Assert.Same(OneMinute, Choose(TimerAction.Restart, "1 min", [], OneMinute, Tea));

        var finished = new ListedTimer("Roast", 0, Started: false, Running: false);
        Assert.Same(finished, Choose(TimerAction.Stop, "Roast", ["Roast"], OneMinute, finished));
        Assert.Null(Choose(TimerAction.Stop, "Roast", [], OneMinute, finished));
    }
}
