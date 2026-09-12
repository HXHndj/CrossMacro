namespace CrossMacro.Platform.Abstractions;

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
