using Pos.App.Input;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// SRS 2.1: typed queries wait roughly 150ms before hitting the database, so a six-character
/// search runs one query rather than six.
/// </summary>
public class SearchDebouncerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(150);

    private static (SearchDebouncer Debouncer, VirtualScheduler Scheduler, List<string> Fired) New()
    {
        var scheduler = new VirtualScheduler();
        var fired = new List<string>();
        var debouncer = new SearchDebouncer(scheduler, fired.Add, Window);

        return (debouncer, scheduler, fired);
    }

    [Fact]
    public void NothingFiresBeforeTheWindowCloses()
    {
        var (debouncer, scheduler, fired) = New();

        debouncer.Notify("da");
        scheduler.Advance(149);

        Assert.Empty(fired);
        Assert.True(debouncer.IsPending);
    }

    [Fact]
    public void TheQueryFiresOnceTheWindowCloses()
    {
        var (debouncer, scheduler, fired) = New();

        debouncer.Notify("dal");
        scheduler.Advance(150);

        Assert.Equal(["dal"], fired);
        Assert.False(debouncer.IsPending);
    }

    /// <summary>
    /// The point of the whole thing: a burst of keystrokes must collapse into one query, carrying
    /// the final text rather than any of the intermediate ones.
    /// </summary>
    [Fact]
    public void KeystrokesInsideTheWindowCollapseIntoASingleQuery()
    {
        var (debouncer, scheduler, fired) = New();

        foreach (var text in new[] { "t", "to", "too", "toor" })
        {
            debouncer.Notify(text);
            scheduler.Advance(40);
        }

        Assert.Empty(fired);

        scheduler.Advance(150);

        Assert.Equal(["toor"], fired);
    }

    [Fact]
    public void PausingLongEnoughBetweenBurstsRunsTwoQueries()
    {
        var (debouncer, scheduler, fired) = New();

        debouncer.Notify("toor");
        scheduler.Advance(150);

        debouncer.Notify("toor dal");
        scheduler.Advance(150);

        Assert.Equal(["toor", "toor dal"], fired);
    }

    /// <summary>The scanner path: a classified scan has nothing to wait for.</summary>
    [Fact]
    public void FlushFiresImmediately()
    {
        var (debouncer, _, fired) = New();

        debouncer.Flush("8901234567890");

        Assert.Equal(["8901234567890"], fired);
    }

    /// <summary>
    /// A scan arriving mid-type must not be followed by the half-typed query landing afterwards.
    /// </summary>
    [Fact]
    public void FlushDiscardsAPendingQuery()
    {
        var (debouncer, scheduler, fired) = New();

        debouncer.Notify("to");
        debouncer.Flush("8901234567890");
        scheduler.Advance(500);

        Assert.Equal(["8901234567890"], fired);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void CancelDropsAPendingQuery()
    {
        var (debouncer, scheduler, fired) = New();

        debouncer.Notify("dal");
        debouncer.Cancel();
        scheduler.Advance(500);

        Assert.Empty(fired);
        Assert.False(debouncer.IsPending);
    }

    /// <summary>Each keystroke restarts the window; a steady typist never triggers a query mid-word.</summary>
    [Fact]
    public void SustainedTypingKeepsPushingTheWindowBack()
    {
        var (debouncer, scheduler, fired) = New();

        for (var i = 0; i < 20; i++)
        {
            debouncer.Notify(new string('a', i + 1));
            scheduler.Advance(100);
        }

        Assert.Empty(fired);

        scheduler.Advance(150);

        Assert.Single(fired);
        Assert.Equal(new string('a', 20), fired[0]);
    }

    [Fact]
    public void CancellingLeavesNoTimerBehind()
    {
        var (debouncer, scheduler, _) = New();

        debouncer.Notify("a");
        Assert.Equal(1, scheduler.PendingCount);

        debouncer.Notify("ab");
        Assert.Equal(1, scheduler.PendingCount);

        debouncer.Cancel();
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void UsingADisposedDebouncerIsRejected()
    {
        var (debouncer, _, _) = New();
        debouncer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => debouncer.Notify("dal"));
        Assert.Throws<ObjectDisposedException>(() => debouncer.Flush("dal"));
    }
}
