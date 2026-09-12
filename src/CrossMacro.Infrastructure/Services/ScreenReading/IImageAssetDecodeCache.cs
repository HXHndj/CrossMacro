namespace CrossMacro.Infrastructure.Services.ScreenReading;

/// <summary>
/// Caches decoded template frames for playback image steps keyed by asset
/// name plus content hash. Loop-heavy scripts otherwise re-inflate and
/// re-unfilter the same embedded PNG on every iteration.
/// </summary>
internal interface IImageAssetDecodeCache
{
    /// <summary>Returns a read-only wrapper sharing the cached pixels; the wrapper carries no ownership.</summary>
    public bool TryGetFrame(string imageName, string base64Png, out ScreenFrame? frame);

    /// <summary>Stores the decoded frame; the cache takes over ownership (dispose happens on eviction/shutdown).</summary>
    public void Store(string imageName, string base64Png, ScreenFrame frame);
}
