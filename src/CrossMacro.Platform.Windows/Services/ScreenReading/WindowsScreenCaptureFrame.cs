namespace CrossMacro.Platform.Windows.Services.ScreenReading;

internal sealed record WindowsScreenCaptureFrame(
    ScreenRect LogicalBounds,
    int Stride,
    ScreenPixelFormat PixelFormat,
    ReadOnlyMemory<byte> Pixels,
    IDisposable? Owner = null) : IDisposable
{
    public void Dispose() => Owner?.Dispose();
}
