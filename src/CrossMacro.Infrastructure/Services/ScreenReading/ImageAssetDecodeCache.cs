namespace CrossMacro.Infrastructure.Services.ScreenReading;

using System.IO.Hashing;

/// <summary>
/// Caches decoded template frames for playback image steps keyed by asset
/// name plus content hash. Loop-heavy scripts otherwise re-inflate and
/// re-unfilter the same embedded PNG on every iteration.
/// </summary>
internal interface IImageAssetDecodeCache
{
    /// <summary>Returns a read-only wrapper sharing the cached pixels; the wrapper carries no ownership.</summary>
    bool TryGetFrame(string imageName, string base64Png, out ScreenFrame frame);

    /// <summary>Stores the decoded frame; the cache takes over ownership (dispose happens on eviction/shutdown).</summary>
    void Store(string imageName, string base64Png, ScreenFrame frame);
}

internal sealed class ImageAssetDecodeCache : IImageAssetDecodeCache, IDisposable
{
    private const int Capacity = 8;

    private readonly Lock _lock = new();
    private readonly Dictionary<(string Name, long Hash), LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _lru = [];

    public bool TryGetFrame(string imageName, string base64Png, out ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(imageName);
        ArgumentNullException.ThrowIfNull(base64Png);
        frame = null!;

        var key = (imageName, ImageAssetCacheKey.Compute(base64Png));
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            frame = node.Value.Frame.CreateNonOwningWrapper();
            return true;
        }
    }

    public void Store(string imageName, string base64Png, ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(imageName);
        ArgumentNullException.ThrowIfNull(base64Png);
        ArgumentNullException.ThrowIfNull(frame);

        var key = (imageName, ImageAssetCacheKey.Compute(base64Png));
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = _lru.AddFirst(new CacheEntry(key, frame));
            _entries[key] = node;
            while (_entries.Count > Capacity && _lru.Last is { } oldest)
            {
                _ = _entries.Remove(oldest.Value.Key);
                _lru.RemoveLast();
                oldest.Value.Frame.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _lru)
            {
                entry.Frame.Dispose();
            }

            _lru.Clear();
            _entries.Clear();
        }
    }

    private readonly record struct CacheEntry((string Name, long Hash) Key, ScreenFrame Frame);
}

internal static class ImageAssetCacheKey
{
    /// <summary>Allocation-free content hash over the UTF-16 base64 payload bytes.</summary>
    public static long Compute(string base64Png) =>
        XxHash128.Hash(MemoryMarshal.Cast<char, byte>(base64Png.AsSpan())) is { Length: 16 } hash ? BitConverter.ToInt64(hash) : 0L;
}

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
