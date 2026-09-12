using System.Diagnostics;

namespace CrossMacro.Platform.Windows.Tests.Services;

public sealed class WindowsWaitableTimerDelayStrategyTests
{
    [WindowsFact]
    public async Task WaitAsync_WhenHighResolutionSupported_WaitsAccuratelyWithoutTimerQuantization()
    {
        using var strategy = new WindowsWaitableTimerDelayStrategy();
        if (!strategy.IsHighResolution)
        {
            // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION requires Windows 10 1803+;
            // the standard fallback timer is still tick-quantized, so skip the
            // precision assertion there.
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        await ((ICoarseDelayStrategy)strategy).WaitAsync(15, CancellationToken.None);
        stopwatch.Stop();

        // A Task.Delay(15) coarse phase wakes on a ~15.6 ms tick boundary
        // (15-31 ms). The high-resolution timer must land well inside that.
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 13, 20);
    }

    [WindowsFact]
    public async Task WaitAsync_WhenCancelledBeforeWait_ThrowsOperationCanceledException()
    {
        using var strategy = new WindowsWaitableTimerDelayStrategy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ((ICoarseDelayStrategy)strategy).WaitAsync(10, cancellation.Token).AsTask());
    }

    [WindowsFact]
    public async Task WaitAsync_WhenCancelledDuringNativeWait_StopsPromptly()
    {
        using var waitStarted = new ManualResetEventSlim(initialState: false);
        using var strategy = new WindowsWaitableTimerDelayStrategy(waitStarted.Set);
        using var cancellation = new CancellationTokenSource();
        var waitTask = Task.Run(
            () => ((ICoarseDelayStrategy)strategy).WaitAsync(500, cancellation.Token).AsTask(),
            CancellationToken.None);

        Assert.True(waitStarted.Wait(TimeSpan.FromSeconds(1), CancellationToken.None));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask.WaitAsync(
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None));
    }

    [WindowsFact]
    public async Task WaitAsync_SequentialWaits_EachWaitForTheRequestedDuration()
    {
        using var strategy = new WindowsWaitableTimerDelayStrategy();

        var stopwatch = Stopwatch.StartNew();
        await ((ICoarseDelayStrategy)strategy).WaitAsync(5, CancellationToken.None);
        await ((ICoarseDelayStrategy)strategy).WaitAsync(5, CancellationToken.None);
        stopwatch.Stop();

        // Two re-armed waits must not return early or signal immediately
        // because the manual-reset handle stayed signaled.
        Assert.True(stopwatch.Elapsed.TotalMilliseconds >= 9);
    }

    [WindowsFact]
    public void Dispose_CanBeCalledTwice()
    {
        var strategy = new WindowsWaitableTimerDelayStrategy();
        strategy.Dispose();
        strategy.Dispose();
    }
}
