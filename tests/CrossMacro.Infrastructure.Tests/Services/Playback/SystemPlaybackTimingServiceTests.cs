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

        await service.WaitAsync(60, pauseToken, CancellationToken.None);

        // A 60 ms wait is chunked into coarse chunks of at most 50 ms, so the
        // strategy must be consulted at least twice with positive delays.
        _ = strategy.RequestedDelays.Should().NotBeEmpty();
        _ = strategy.RequestedDelays.Should().OnlyContain(delay => delay >= 1);
        _ = strategy.CallCount.Should().BeGreaterThanOrEqualTo(2);
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
