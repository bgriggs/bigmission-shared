using BigMission.Shared.Utilities;

namespace BigMission.Shared.Tests.Utilities;

/// <summary>
/// <see cref="Debouncer"/> collapses a burst of triggers into one deferred call. These tests pin which
/// call in a burst wins, that the window reopens afterwards, and how the debouncer behaves when the
/// action or the wait goes wrong - the last of which decides whether a debouncer keeps working for the
/// life of the process.
/// </summary>
/// <remarks>
/// The waits here are real, so the delays are kept short and the assertions coarse. Anything that
/// needed a tighter margin than these would be measuring the scheduler rather than the debouncer.
/// </remarks>
[TestClass]
public class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    #region Suppression

    [TestMethod]
    public async Task ExecuteAsync_SingleCall_RunsTheActionAfterTheDelay()
    {
        var debouncer = new Debouncer(Delay);
        var ran = false;

        var start = Environment.TickCount64;
        await debouncer.ExecuteAsync(() => { ran = true; return Task.CompletedTask; });
        var elapsed = Environment.TickCount64 - start;

        Assert.IsTrue(ran);
        Assert.IsTrue(elapsed >= Delay.TotalMilliseconds - 20, $"The action should have waited out the delay, but took {elapsed}ms.");
    }

    [TestMethod]
    public async Task ExecuteAsync_BurstOfCalls_RunsOnlyTheFirst()
    {
        var debouncer = new Debouncer(Delay);
        var runs = 0;
        Task Action() { Interlocked.Increment(ref runs); return Task.CompletedTask; }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => debouncer.ExecuteAsync(Action)));

        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    public async Task ExecuteAsync_CalledConcurrentlyFromManyThreads_LetsExactlyOneCallerThrough()
    {
        // The burst test above only looks concurrent: awaiting a sequence of ExecuteAsync calls runs
        // each one on the calling thread up to its first incomplete await, so the lock is never
        // contended and a broken one would go unnoticed. Task.Run puts the callers on separate thread
        // pool threads, which is what the documented thread safety is actually about.
        var debouncer = new Debouncer(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => debouncer.ExecuteAsync(() => Task.CompletedTask, cancellation.Token)))
            .ToArray();

        // All but the one holding the window open should return without waiting.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (callers.Count(c => c.IsCompleted) < 31 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual(31, callers.Count(c => c.IsCompleted), "Exactly one caller should be left waiting out the delay.");

        await cancellation.CancelAsync();
        foreach (var caller in callers)
        {
            try { await caller; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_CallsSuppressedDuringTheWindow_ReturnImmediately()
    {
        // A suppressed caller must not be left waiting out the window, because callers await this on
        // the path that triggered them.
        var debouncer = new Debouncer(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var first = debouncer.ExecuteAsync(() => Task.CompletedTask, cancellation.Token);

        var start = Environment.TickCount64;
        await debouncer.ExecuteAsync(() => Task.CompletedTask);
        var elapsed = Environment.TickCount64 - start;

        Assert.IsLessThan(1000, elapsed, "A suppressed call should return without waiting out the delay.");
        Assert.IsFalse(first.IsCompleted);

        // Cut the window short rather than leaving its timer running into whatever test comes next.
        await cancellation.CancelAsync();
        try { await first; } catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task ExecuteAsync_AfterTheWindowCloses_RunsAgain()
    {
        var debouncer = new Debouncer(Delay);
        var runs = 0;
        Task Action() { Interlocked.Increment(ref runs); return Task.CompletedTask; }

        await debouncer.ExecuteAsync(Action);
        await debouncer.ExecuteAsync(Action);

        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public async Task ExecuteAsync_ItIsTheFirstCallersActionThatRuns()
    {
        // Trailing-edge debouncers run the last action; this one runs the first and drops the rest, so
        // a caller passing a closure over changing state gets the oldest value, not the newest.
        var debouncer = new Debouncer(Delay);
        string? ran = null;

        var first = debouncer.ExecuteAsync(() => { ran = "first"; return Task.CompletedTask; });
        await debouncer.ExecuteAsync(() => { ran = "second"; return Task.CompletedTask; });
        await first;

        Assert.AreEqual("first", ran);
    }

    #endregion

    #region Disabled

    [TestMethod]
    public async Task ExecuteAsync_WhenDisabled_RunsTheActionWithoutWaiting()
    {
        // Disabled turns off the debouncing, not the work. A caller that flips this flag is asking for
        // its action to run every time, so dropping the call would silently lose it.
        var debouncer = new Debouncer(TimeSpan.FromSeconds(5)) { IsDisabled = true };
        var ran = false;

        var start = Environment.TickCount64;
        await debouncer.ExecuteAsync(() => { ran = true; return Task.CompletedTask; });
        var elapsed = Environment.TickCount64 - start;

        Assert.IsTrue(ran);
        Assert.IsTrue(elapsed < 1000, $"The action should not have waited out the delay, but took {elapsed}ms.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenDisabled_SuppressesNothing()
    {
        var debouncer = new Debouncer(Delay) { IsDisabled = true };
        var runs = 0;
        Task Action() { Interlocked.Increment(ref runs); return Task.CompletedTask; }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => debouncer.ExecuteAsync(Action)));

        Assert.AreEqual(20, runs);
    }

    [TestMethod]
    public async Task ExecuteAsync_DisabledCall_DoesNotOpenAWindowThatSuppressesTheNextOne()
    {
        // The disabled path returns before the waiting flag is touched, so re-enabling mid-burst does
        // not find the debouncer already holding a window open.
        var debouncer = new Debouncer(Delay) { IsDisabled = true };
        var runs = 0;

        await debouncer.ExecuteAsync(() => { runs++; return Task.CompletedTask; });
        debouncer.IsDisabled = false;
        await debouncer.ExecuteAsync(() => { runs++; return Task.CompletedTask; });

        Assert.AreEqual(2, runs);
    }

    #endregion

    #region Cancellation and faults

    [TestMethod]
    public async Task ExecuteAsync_TokenAlreadyCanceled_ThrowsWithoutRunningTheAction()
    {
        var debouncer = new Debouncer(Delay);
        var ran = false;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => debouncer.ExecuteAsync(() => { ran = true; return Task.CompletedTask; }, cancellation.Token));

        Assert.IsFalse(ran);
    }

    [TestMethod]
    public async Task ExecuteAsync_CanceledDuringTheWait_LeavesTheDebouncerUsable()
    {
        // Cancellation is the ordinary way out for a caller passing a stopping token. The waiting flag
        // is cleared in a finally so this does not suppress every later call for the life of the
        // instance, which is what a debouncer shared across a reconnect would otherwise do.
        // The delay is short because the follow-up call has to wait it out to prove the point.
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(500));
        using var cancellation = new CancellationTokenSource();

        var canceled = debouncer.ExecuteAsync(() => Task.CompletedTask, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceled);

        var ran = false;
        await debouncer.ExecuteAsync(() => { ran = true; return Task.CompletedTask; });

        Assert.IsTrue(ran);
    }

    [TestMethod]
    public async Task ExecuteAsync_CanceledDuringTheWait_DoesNotRunTheAction()
    {
        var debouncer = new Debouncer(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var ran = false;

        var canceled = debouncer.ExecuteAsync(() => { ran = true; return Task.CompletedTask; }, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceled);

        Assert.IsFalse(ran);
    }

    [TestMethod]
    public async Task ExecuteAsync_ActionThrows_SurfacesTheFaultAndLeavesTheDebouncerUsable()
    {
        // The flag is cleared before the action runs, so the action's own failure is the caller's to
        // handle and leaves the debouncer ready for the next call.
        var debouncer = new Debouncer(Delay);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => debouncer.ExecuteAsync(() => throw new InvalidOperationException("boom")));

        var ran = false;
        await debouncer.ExecuteAsync(() => { ran = true; return Task.CompletedTask; });

        Assert.IsTrue(ran);
    }

    #endregion
}
