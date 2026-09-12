namespace CrossMacro.Infrastructure.Tests.Services.Playback;

public sealed class MotionTrajectoryResamplerTests
{

    [Fact]
    public void CreatePlan_LargeDenseTrajectory_CompletesInLinearTime()
    {
        // 60s @ ~1kHz source resampled to 500/s: the previous per-sample linear
        // scan was O(n*m) (~1.8e9 inner iterations, seconds of CPU); the monotonic
        // cursor keeps this in the milliseconds range.
        var events = new List<MacroEvent>(60_000);
        for (var index = 0; index < 60_000; index++)
        {
            events.Add(new MacroEvent
            {
                Type = EventType.MouseMove,
                X = index,
                Y = (index * 7) % 1000,
                TimestampMicroseconds = index * 1_000L,
                DelayMicroseconds = 1_000,
            });
        }

        var macro = new MacroSequence { IsAbsoluteCoordinates = true };
        macro.ReplaceEvents(events);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var plan = MotionTrajectoryResampler.CreatePlan(
            macro,
            speedMultiplier: 1,
            new PlaybackOptions
            {
                MotionMode = MotionPlaybackMode.StrictSpeed,
                StrictSpeedMotionEventsPerSecond = 500,
            });
        stopwatch.Stop();

        _ = plan.ResampledSegmentCount.Should().BeGreaterThan(0);
        _ = stopwatch.Elapsed.TotalSeconds.Should().BeLessThan(15);
    }

    [Fact]
    public void CreatePlan_StrictSpeedMode_NonAdvancingSegmentsDoNotBreakInterpolation()
    {
        // Raw delays can be non-positive; those segments never match and the
        // cursor must skip them without corrupting later interpolation.
        var macro = new MacroSequence { IsAbsoluteCoordinates = true };
        macro.ReplaceEvents(
        [
            new MacroEvent { Type = EventType.MouseMove, X = 0, Y = 0, DelayMicroseconds = 0, TimestampMicroseconds = 0 },
            new MacroEvent { Type = EventType.MouseMove, X = 100, Y = 0, DelayMicroseconds = 10_000, TimestampMicroseconds = 10_000 },
            new MacroEvent { Type = EventType.MouseMove, X = 200, Y = 0, DelayMicroseconds = -4_000, TimestampMicroseconds = 6_000 },
            new MacroEvent { Type = EventType.MouseMove, X = 300, Y = 0, DelayMicroseconds = 6_000, TimestampMicroseconds = 12_000 },
        ]);

        var plan = MotionTrajectoryResampler.CreatePlan(
            macro,
            speedMultiplier: 1,
            new PlaybackOptions
            {
                MotionMode = MotionPlaybackMode.StrictSpeed,
                StrictSpeedMotionEventsPerSecond = 100,
            });

        // Monotonic timestamps and positive delays across the whole plan.
        long previousTimestamp = -1;
        foreach (var ev in plan.Events)
        {
            _ = ev.TimestampMicroseconds.Should().BeGreaterThanOrEqualTo(previousTimestamp);
            _ = ev.DelayMicroseconds.Should().BeGreaterThanOrEqualTo(0);
            previousTimestamp = ev.TimestampMicroseconds;
        }
    }

    [Fact]
    public void CreatePlan_PrecisionMode_PreservesEveryRecordedSample()
    {
        var macro = CreateDenseAbsoluteTrajectory();

        var plan = MotionTrajectoryResampler.CreatePlan(
            macro,
            speedMultiplier: 10,
            new PlaybackOptions { MotionMode = MotionPlaybackMode.Precision });

        _ = plan.Events.Should().Equal(macro.Events);
        _ = plan.ResampledSegmentCount.Should().Be(0);
        _ = plan.OmittedSampleCount.Should().Be(0);
    }

    [Fact]
    public void CreatePlan_StrictSpeedMode_InterpolatesTrajectoryAtTheBoundedOutputRate()
    {
        var macro = CreateDenseAbsoluteTrajectory();

        var plan = MotionTrajectoryResampler.CreatePlan(
            macro,
            speedMultiplier: 1,
            new PlaybackOptions
            {
                MotionMode = MotionPlaybackMode.StrictSpeed,
                StrictSpeedMotionEventsPerSecond = 100,
            });

        _ = plan.Events.Select(static ev => (ev.X, ev.Y)).Should().Equal((0, 0), (100, 0), (200, 0), (250, 0));
        _ = plan.Events.Select(static ev => ev.DelayMicroseconds).Should().Equal(0, 10_000, 10_000, 5_000);
        _ = plan.Events.Select(static ev => ev.TimestampMicroseconds).Should().Equal(0, 10_000, 20_000, 25_000);
        _ = plan.ResampledSegmentCount.Should().Be(1);
        _ = plan.OmittedSampleCount.Should().Be(0);
        _ = plan.IsErrorBoundSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CreatePlan_StrictSpeedMode_ReportsWhenFixedRateCannotRetainALoopWithinThePixelBudget()
    {
        var macro = new MacroSequence { IsAbsoluteCoordinates = true };
        macro.Events.Add(CreateMove(0, 0, 0));
        macro.Events.Add(new MacroEvent { Type = EventType.MouseMove, X = 0, Y = 40, TimestampMicroseconds = 5_000, DelayMicroseconds = 5_000 });
        macro.Events.Add(new MacroEvent { Type = EventType.MouseMove, X = 40, Y = 40, TimestampMicroseconds = 10_000, DelayMicroseconds = 5_000 });
        macro.Events.Add(new MacroEvent { Type = EventType.MouseMove, X = 40, Y = 0, TimestampMicroseconds = 15_000, DelayMicroseconds = 5_000 });
        macro.Events.Add(new MacroEvent { Type = EventType.MouseMove, X = 0, Y = 0, TimestampMicroseconds = 20_000, DelayMicroseconds = 5_000 });

        var plan = MotionTrajectoryResampler.CreatePlan(
            macro,
            speedMultiplier: 1,
            new PlaybackOptions
            {
                MotionMode = MotionPlaybackMode.StrictSpeed,
                StrictSpeedMotionEventsPerSecond = 60,
                MaximumMotionErrorPixels = 1d,
            });

        _ = plan.IsErrorBoundSatisfied.Should().BeFalse();
        _ = plan.MaximumGeometricErrorPixels.Should().BeGreaterThan(1d);
    }

    [Fact]
    public void CreatePlan_StrictSpeedMode_ResamplesLogicalRelativeMovesWithoutDroppingTheInitialDelta()
    {
        var macro = new MacroSequence { IsAbsoluteCoordinates = false };
        for (int index = 0; index < 5; index++)
        {
            macro.Events.Add(new MacroEvent
            {
                Type = EventType.MouseMove,
                X = 10,
                Y = 0,
                CoordinateMode = MouseCoordinateMode.Relative,
                CoordinateSpace = MouseCoordinateSpace.LogicalDesktop,
                TimestampMicroseconds = index * 5_000,
                DelayMicroseconds = index is 0 ? 0 : 5_000,
            });
        }

        var plan = MotionTrajectoryResampler.CreatePlan(
            macro,
            speedMultiplier: 1,
            new PlaybackOptions
            {
                MotionMode = MotionPlaybackMode.StrictSpeed,
                StrictSpeedMotionEventsPerSecond = 100,
            });

        _ = plan.Events.Select(static ev => (ev.X, ev.Y)).Should().Equal((10, 0), (20, 0), (20, 0));
        _ = plan.Events.Select(static ev => ev.DelayMicroseconds).Should().Equal(0, 10_000, 10_000);
        _ = plan.Events.Select(static ev => ev.CoordinateMode).Should().OnlyContain(
            mode => mode == MouseCoordinateMode.Relative);
        _ = plan.Events.Select(static ev => ev.CoordinateSpace).Should().OnlyContain(
            space => space == MouseCoordinateSpace.LogicalDesktop);
    }

    private static MacroSequence CreateDenseAbsoluteTrajectory()
    {
        return new MacroSequence
        {
            IsAbsoluteCoordinates = true,
            Events =
            {
                CreateMove(x: 0, timestampMicroseconds: 0, delayMicroseconds: 0),
                CreateMove(x: 120, timestampMicroseconds: 12_000, delayMicroseconds: 12_000),
                CreateMove(x: 240, timestampMicroseconds: 24_000, delayMicroseconds: 12_000),
                CreateMove(x: 250, timestampMicroseconds: 25_000, delayMicroseconds: 1_000),
            },
        };
    }

    private static MacroEvent CreateMove(int x, long timestampMicroseconds, long delayMicroseconds)
    {
        return new MacroEvent
        {
            Type = EventType.MouseMove,
            X = x,
            Y = 0,
            Timestamp = MacroTiming.ToLegacyTimestampMilliseconds(timestampMicroseconds),
            TimestampMicroseconds = timestampMicroseconds,
            DelayMs = MacroTiming.ToLegacyMilliseconds(delayMicroseconds),
            DelayMicroseconds = delayMicroseconds,
        };
    }
}
