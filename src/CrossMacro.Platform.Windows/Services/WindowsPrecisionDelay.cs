using System.Threading;

namespace CrossMacro.Platform.Windows.Services;

/// <summary>
/// Absolute-deadline precision delay for trajectory pacing: coarse chunks go
/// through the high-resolution waitable timer strategy, the final 0.5 ms spins.
/// Mirrors the playback timing service policy without taking an Infrastructure
/// dependency.
/// </summary>
internal static class WindowsPrecisionDelay
{
    private const double FinalSpinWindowMilliseconds = 0.5d;
    private const int MaximumCoarseDelayMilliseconds = 50;

    // Lazily created; the waitable timer handle is process-scoped and the
    // strategy serializes access internally, so one shared instance is enough.
    // A rare double-factory race would only leak one finalizer-reclaimed handle.
    private static WindowsWaitableTimerDelayStrategy? _strategy;

    public static async Task WaitUntilAsync(long deadlineTicks, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = remainingTicks * 1_000d / Stopwatch.Frequency;
            var coarseDelayMilliseconds = Math.Min(
                MaximumCoarseDelayMilliseconds,
                Math.Max(0, Convert.ToInt32(Math.Floor(remainingMilliseconds - FinalSpinWindowMilliseconds))));
            if (coarseDelayMilliseconds > 0)
            {
                var strategy = LazyInitializer.EnsureInitialized(
                    ref _strategy,
                    static () => new WindowsWaitableTimerDelayStrategy());
                await strategy.WaitAsync(coarseDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var spinner = new SpinWait();
            while (Stopwatch.GetTimestamp() < deadlineTicks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                spinner.SpinOnce(sleep1Threshold: -1);
            }

            return;
        }
    }
}
