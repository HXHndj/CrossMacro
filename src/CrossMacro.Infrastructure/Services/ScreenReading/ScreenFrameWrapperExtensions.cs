namespace CrossMacro.Infrastructure.Services.ScreenReading;

internal static class ScreenFrameWrapperExtensions
{
    /// <summary>Builds an equivalent frame over the same pixel memory without ownership.</summary>
    public static ScreenFrame CreateNonOwningWrapper(this ScreenFrame source) => new(
        source.LogicalBounds,
        source.Stride,
        source.PixelFormat,
        source.Pixels,
        owner: null,
        source.ValidPixelMask,
        validityIndex: null,
        source.AlphaMode);
}
