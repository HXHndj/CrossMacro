namespace CrossMacro.Platform.Abstractions;

public enum InputEventType
{
    Sync = 0,

    Key = 1,

    MouseButton = 2,

    MouseMove = 3,

    MouseScroll = 4,

    /// <summary>
    /// Two-axis mouse movement in a single event: <c>Value</c> carries X (or dx)
    /// and <c>ValueY</c> carries Y (or dy); <c>Code</c> selects ABS_X (absolute)
    /// or REL_X (relative) semantics. Capture backends whose native events
    /// already carry both axes (Windows hooks, raw input) emit this instead of
    /// separate axis events plus Sync.
    /// </summary>
    MouseMove2D = 5,

    // Preserve protocol compatibility while making unknown events explicit.
    Unknown = 255,
}
