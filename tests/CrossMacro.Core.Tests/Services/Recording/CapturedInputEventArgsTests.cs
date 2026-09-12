namespace CrossMacro.Core.Tests.Services.Recording;

public sealed class CapturedInputEventArgsTests
{
    [Fact]
    public void Event_RoundTrip_PreservesEveryFieldIncludingSecondAxis()
    {
        // Regression guard: CapturedInputEventArgs stores fields individually and
        // rebuilds the struct; a field added to CapturedInputEvent but not mirrored
        // here is silently zeroed (a real defect once shipped MouseMove2D without
        // ValueY and playback collapsed to horizontal-only movement).
        var captured = new CapturedInputEvent
        {
            Type = InputEventType.MouseMove2D,
            Code = InputEventCode.ABS_X,
            Value = 1042,
            ValueY = 733,
            Timestamp = 12_345,
            TimestampMicroseconds = 12_345_678,
            DeviceName = "VirtualMouse",
        };

        var args = new CapturedInputEventArgs(captured);
        var roundTripped = args.Event;

        _ = roundTripped.Should().Be(captured);
        _ = args.ValueY.Should().Be(733);
    }

    [Fact]
    public void Event_RoundTrip_DefaultValueYIsZeroForSingleAxisEvents()
    {
        var captured = new CapturedInputEvent
        {
            Type = InputEventType.Key,
            Code = InputEventCode.KEY_A,
            Value = 1,
        };

        var args = new CapturedInputEventArgs(captured);

        _ = args.Event.ValueY.Should().Be(0);
    }
}
