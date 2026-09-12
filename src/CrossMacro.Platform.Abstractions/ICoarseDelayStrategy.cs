namespace CrossMacro.Platform.Abstractions;

/// <summary>
/// Provides the coarse (non-spinning) portion of a high-resolution delay.
/// Implementations should sleep as close to the requested duration as the
/// platform allows; callers keep responsibility for the final sub-millisecond
/// spin so the two phases compose into one accurate deadline.
/// </summary>
/// <remarks>
/// On Windows the default <c>Task.Delay</c> coarse phase is quantized to the
/// system timer period (~15.6 ms). A high-resolution waitable timer strategy
/// removes that quantization for playback timing without any process-wide
/// timer resolution changes.
/// </remarks>
public interface ICoarseDelayStrategy
{
    /// <summary>Waits for approximately <paramref name="millisecondsDelay"/> milliseconds.</summary>
    /// <param name="millisecondsDelay">Delay in milliseconds; always at least 1.</param>
    /// <param name="cancellationToken">Observed between internally chunked waits; cancellation latency is bounded by the chunk length.</param>
    ValueTask WaitAsync(int millisecondsDelay, CancellationToken cancellationToken);
}

/// <summary>Default coarse delay backed by <see cref="Task.Delay"/> (system timer granularity).</summary>
public sealed class TaskDelayCoarseDelayStrategy : ICoarseDelayStrategy
{
    public static TaskDelayCoarseDelayStrategy Instance { get; } = new();

    async ValueTask ICoarseDelayStrategy.WaitAsync(int millisecondsDelay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(millisecondsDelay, 0);
        await Task.Delay(millisecondsDelay, cancellationToken).ConfigureAwait(false);
    }
}
