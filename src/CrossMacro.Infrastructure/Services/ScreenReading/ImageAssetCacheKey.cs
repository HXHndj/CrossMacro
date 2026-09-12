using System.IO.Hashing;

namespace CrossMacro.Infrastructure.Services.ScreenReading;

internal static class ImageAssetCacheKey
{
    /// <summary>Allocation-free content hash over the UTF-16 base64 payload bytes.</summary>
    public static long Compute(string base64Png) =>
        XxHash128.Hash(MemoryMarshal.Cast<char, byte>(base64Png.AsSpan())) is { Length: 16 } hash ? BitConverter.ToInt64(hash) : 0L;
}
