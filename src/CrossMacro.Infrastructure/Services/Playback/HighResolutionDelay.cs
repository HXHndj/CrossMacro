namespace CrossMacro.Infrastructure.Services.Playback;

/// <summary>Provides cancellation-aware high-resolution delays.</summary>
internal static class HighResolutionDelay
{
    private const double FinalSpinWindowMilliseconds = 0.5d;
    private const int MaximumCoarseDelayMilliseconds = 50;

    public static Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        WaitAsync(delay, cancellationToken, coarseDelayStrategy: null);

    public static async Task WaitAsync(
        TimeSpan delay,
        CancellationToken cancellationToken,
        ICoarseDelayStrategy? coarseDelayStrategy)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var coarse = coarseDelayStrategy ?? TaskDelayCoarseDelayStrategy.Instance;
        cancellationToken.ThrowIfCancellationRequested();
        var deadlineTicks = Stopwatch.GetTimestamp() + ToStopwatchTicks(delay);

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
            if (coarseDelayMilliseconds >= 1)
            {
                await coarse.WaitAsync(coarseDelayMilliseconds, cancellationToken).ConfigureAwait(false);
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

    private static long ToStopwatchTicks(TimeSpan delay) =>
        checked((long)Math.Ceiling(delay.TotalSeconds * Stopwatch.Frequency));
}
