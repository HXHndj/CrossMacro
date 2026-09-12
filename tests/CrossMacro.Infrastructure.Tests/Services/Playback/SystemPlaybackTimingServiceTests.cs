namespace CrossMacro.Infrastructure.Tests.Services.Playback;

public sealed class SystemPlaybackTimingServiceTests
{
    [Fact]
    public async Task WaitAsync_WhenDelayIsZero_ReturnsImmediately()
    {
        var service = new SystemPlaybackTimingService();
        var pauseToken = new FakePauseToken();

        await service.WaitAsync(0, pauseToken, CancellationToken.None);

        _ = pauseToken.WaitCallCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitAsync_WhenPaused_ResumesAfterPauseTokenCompletes()
    {
        var service = new SystemPlaybackTimingService();
        var pauseToken = new FakePauseToken { IsPaused = true };

        await service.WaitAsync(2, pauseToken, CancellationToken.None);

        _ = pauseToken.WaitCallCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var service = new SystemPlaybackTimingService();
        var pauseToken = new FakePauseToken();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = async () => await service.WaitAsync(100, pauseToken, cancellation.Token);

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitAsync_WhenCoarseDelayStrategyProvided_UsesItForCoarseChunks()
    {
        var strategy = new CountingCoarseDelayStrategy();
        var service = new SystemPlaybackTimingService(strategy);
        var pauseToken = new FakePauseToken();

        await service.WaitAsync(120, pauseToken, CancellationToken.None);

        // Deterministic regardless of thread-pool load: the first chunk is the
        // full MaxDelayChunkMs, every coarse request is positive, and none
        // exceeds the chunk cap. (Requiring >=2 calls was flaky under load
        // because a single delayed Task.Delay continuation can exhaust the
        // remaining deadline.)
        _ = strategy.RequestedDelays.Should().NotBeEmpty();
        _ = strategy.RequestedDelays[0].Should().Be(50);
        _ = strategy.RequestedDelays.Should().OnlyContain(delay => delay >= 1);
        _ = strategy.RequestedDelays.Should().OnlyContain(delay => delay <= 50);
    }

    private sealed class CountingCoarseDelayStrategy : ICoarseDelayStrategy
    {
        private readonly List<int> _delays = [];

        public int CallCount => _delays.Count;
        public IReadOnlyList<int> RequestedDelays => _delays;

        public async ValueTask WaitAsync(int millisecondsDelay, CancellationToken cancellationToken)
        {
            _delays.Add(millisecondsDelay);
            await Task.Delay(millisecondsDelay, cancellationToken);
        }
    }

    private sealed class FakePauseToken : IPlaybackPauseToken
    {
        public bool IsPaused { get; set; }
        public int WaitCallCount { get; private set; }

        public Task WaitIfPausedAsync(CancellationToken cancellationToken)
        {
            WaitCallCount++;
            IsPaused = false;
            return Task.CompletedTask;
        }
    }
}
