
using System.IO.Hashing;

namespace CrossMacro.Infrastructure.Services.ScreenCapture;

public static class ScreenFramePngEncoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Encode(ScreenFrame frame, Stream output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        ValidateFrame(frame);

        output.Write(PngSignature);
        WriteIhdr(output, frame.Width, frame.Height, UsesAlpha(frame));
        WriteIdat(output, frame);
        WriteIend(output);
    }

    public static async Task EncodeAsync(ScreenFrame frame, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        ValidateFrame(frame);

        await output.WriteAsync(PngSignature, cancellationToken).ConfigureAwait(false);
        await WriteIhdrAsync(output, frame.Width, frame.Height, UsesAlpha(frame), cancellationToken).ConfigureAwait(false);
        await WriteIdatAsync(output, frame, cancellationToken).ConfigureAwait(false);
        await WriteIendAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteIhdrAsync(Stream output, int width, int height, bool hasAlpha, CancellationToken cancellationToken)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8;
        data[9] = hasAlpha ? (byte)6 : (byte)2;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        await WriteChunkAsync(output, "IHDR"u8.ToArray(), data, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteIhdr(Stream output, int width, int height, bool hasAlpha)
    {
        Span<byte> data = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data, width);
        BinaryPrimitives.WriteInt32BigEndian(data[4..], height);
        data[8] = 8;
        data[9] = hasAlpha ? (byte)6 : (byte)2;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        WriteChunk(output, "IHDR"u8, data);
    }

    private static async Task WriteIdatAsync(Stream output, ScreenFrame frame, CancellationToken cancellationToken)
    {
        using var idatBuffer = new MemoryStream();
        uint adler32;
        using (var deflate = new DeflateStream(idatBuffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            adler32 = await WriteFilteredScanlinesAsync(deflate, frame, cancellationToken).ConfigureAwait(false);
        }

        using var zlibBuffer = new MemoryStream();
        zlibBuffer.WriteByte(0x78);
        zlibBuffer.WriteByte(0x01);
        idatBuffer.Position = 0;
        await idatBuffer.CopyToAsync(zlibBuffer, cancellationToken).ConfigureAwait(false);

        var adler = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, adler32);
        await zlibBuffer.WriteAsync(adler, cancellationToken).ConfigureAwait(false);

        await WriteChunkAsync(output, "IDAT"u8.ToArray(), zlibBuffer.GetBuffer().AsMemory(0, (int)zlibBuffer.Length), cancellationToken).ConfigureAwait(false);
    }

    private static void WriteIdat(Stream output, ScreenFrame frame)
    {
        using var idatBuffer = new MemoryStream();
        uint adler32;
        using (var deflate = new DeflateStream(idatBuffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            adler32 = WriteFilteredScanlines(deflate, frame);
        }

        using var zlibBuffer = new MemoryStream();
        zlibBuffer.WriteByte(0x78);
        zlibBuffer.WriteByte(0x01);
        idatBuffer.Position = 0;
        idatBuffer.CopyTo(zlibBuffer);

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, adler32);
        zlibBuffer.Write(adler);
        WriteChunk(output, "IDAT"u8, zlibBuffer.GetBuffer().AsSpan(0, (int)zlibBuffer.Length));
    }

    private static Task WriteIendAsync(Stream output, CancellationToken cancellationToken) =>
        WriteChunkAsync(output, "IEND"u8.ToArray(), ReadOnlyMemory<byte>.Empty, cancellationToken);

    private static void WriteIend(Stream output) => WriteChunk(output, "IEND"u8, []);

    private static async Task<uint> WriteFilteredScanlinesAsync(Stream deflate, ScreenFrame frame, CancellationToken cancellationToken)
    {
        uint a = 1, b = 0;
        // Carry the frame as Memory (hoistable across awaits) and take the span
        // per row; this avoids the full-frame ToArray() copy of the old path.
        var pixels = frame.Pixels;
        var bpp = ScreenFrame.GetBytesPerPixel(frame.PixelFormat);
        var hasAlpha = UsesAlpha(frame);
        var channels = hasAlpha ? 4 : 3;
        var row = new byte[checked(frame.Width * channels)];
        var filtered = new byte[row.Length];
        var filterByte = new byte[1];

        for (var y = 0; y < frame.Height; y++)
        {
            var rowOffset = y * frame.Stride;
            ConvertRowToPng(pixels.Span, rowOffset, frame.Width, bpp, frame.PixelFormat, frame.AlphaMode, row);
            var filterType = TrySubFilterRow(row, filtered, channels, out var encodedRow);
            filterByte[0] = filterType;

            await deflate.WriteAsync(filterByte, cancellationToken).ConfigureAwait(false);
            AdlerChunk(ref a, ref b, filterByte);
            await deflate.WriteAsync(encodedRow, cancellationToken).ConfigureAwait(false);
            AdlerChunk(ref a, ref b, encodedRow);
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Sub-filters the row into <paramref name="filtered"/> and picks whichever
    /// of None/Sub has the smaller biased absolute sum (the standard PNG filter
    /// heuristic). Sub dominates on UI/screen content and typically shrinks the
    /// deflated stream 30%+ versus filter 0.
    /// </summary>
    private static byte TrySubFilterRow(byte[] row, byte[] filtered, int channels, out byte[] encodedRow)
    {
        long costNone = 0;
        long costSub = 0;
        for (var i = 0; i < row.Length; i++)
        {
            var raw = row[i];
            filtered[i] = unchecked((byte)(raw - (i >= channels ? row[i - channels] : 0)));
            costNone += raw;
            costSub += filtered[i];
        }

        if (costSub < costNone)
        {
            encodedRow = filtered;
            return 1;
        }

        encodedRow = row;
        return 0;
    }

    private static uint WriteFilteredScanlines(Stream deflate, ScreenFrame frame)
    {
        uint a = 1, b = 0;
        var pixels = frame.Pixels.Span;
        var bpp = ScreenFrame.GetBytesPerPixel(frame.PixelFormat);
        var hasAlpha = UsesAlpha(frame);
        var channels = hasAlpha ? 4 : 3;
        var row = new byte[checked(frame.Width * channels)];
        var filtered = new byte[row.Length];

        for (var y = 0; y < frame.Height; y++)
        {
            var rowOffset = y * frame.Stride;
            ConvertRowToPng(pixels, rowOffset, frame.Width, bpp, frame.PixelFormat, frame.AlphaMode, row);
            var filterType = TrySubFilterRow(row, filtered, channels, out var encodedRow);
            Span<byte> filterByte = stackalloc byte[1];
            filterByte[0] = filterType;
            deflate.WriteByte(filterType);
            AdlerChunk(ref a, ref b, filterByte);
            deflate.Write(encodedRow, 0, encodedRow.Length);
            AdlerChunk(ref a, ref b, encodedRow);
        }

        return (b << 16) | a;
    }

    private static void ConvertRowToPng(
        ReadOnlySpan<byte> pixels,
        int rowOffset,
        int width,
        int bpp,
        ScreenPixelFormat format,
        ScreenAlphaMode alphaMode,
        byte[] target)
    {
        var hasAlpha = target.Length == checked(width * 4);
        for (var x = 0; x < width; x++)
        {
            var srcOffset = rowOffset + (x * bpp);
            var dstOffset = x * (hasAlpha ? 4 : 3);
            byte red;
            byte green;
            byte blue;

            switch (format)
            {
                case ScreenPixelFormat.Rgb24:
                case ScreenPixelFormat.Abgr8888:
                case ScreenPixelFormat.Xbgr8888:
                    red = pixels[srcOffset];
                    green = pixels[srcOffset + 1];
                    blue = pixels[srcOffset + 2];
                    break;
                case ScreenPixelFormat.Bgr24:
                case ScreenPixelFormat.Xrgb8888:
                case ScreenPixelFormat.Bgra8888:
                    red = pixels[srcOffset + 2];
                    green = pixels[srcOffset + 1];
                    blue = pixels[srcOffset];
                    break;
                default:
                    throw new NotSupportedException($"Unsupported pixel format: {format}");
            }

            if (hasAlpha && alphaMode is ScreenAlphaMode.Premultiplied)
            {
                var alpha = pixels[srcOffset + 3];
                red = Unpremultiply(red, alpha);
                green = Unpremultiply(green, alpha);
                blue = Unpremultiply(blue, alpha);
            }

            target[dstOffset] = red;
            target[dstOffset + 1] = green;
            target[dstOffset + 2] = blue;
            if (hasAlpha)
            {
                target[dstOffset + 3] = pixels[srcOffset + 3];
            }
        }
    }

    private static bool UsesAlpha(ScreenFrame frame) =>
        frame.HasAlphaChannel
        && (frame.AlphaMode is ScreenAlphaMode.Straight or ScreenAlphaMode.Premultiplied);

    private static void ValidateFrame(ScreenFrame frame)
    {
        ScreenImageAssetPolicy.ValidateDimensions(frame.Width, frame.Height);
        var bytesPerPixel = UsesAlpha(frame) ? 4 : 3;
        var pixelBytes = checked((long)frame.Width * frame.Height * bytesPerPixel);
        if (pixelBytes > ScreenImageAssetPolicy.MaxPixelBytes)
        {
            throw new InvalidDataException($"PNG pixel data exceeds the maximum supported size of {ScreenImageAssetPolicy.MaxPixelBytes} bytes.");
        }
    }

    private static byte Unpremultiply(byte value, byte alpha) => alpha is 0 ? (byte)0 : (byte)Math.Min(byte.MaxValue, ((value * 255) + (alpha / 2)) / alpha);

    private const uint AdlerModulus = 65521;
    private const int AdlerMaxChunk = 5552;

    /// <summary>
    /// Batched Adler-32 update (NMAX blocking per RFC 1950): two modulo
    /// operations per 5552 bytes instead of two per byte.
    /// </summary>
    private static void AdlerChunk(ref uint a, ref uint b, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var chunkLength = Math.Min(data.Length, AdlerMaxChunk);
            var chunk = data[..chunkLength];
            ulong sumA = a;
            ulong sumB = b;
            foreach (var value in chunk)
            {
                sumA += value;
                sumB += sumA;
            }

            a = (uint)(sumA % AdlerModulus);
            b = (uint)(sumB % AdlerModulus);
            data = data[chunkLength..];
        }
    }

    private static async Task WriteChunkAsync(Stream output, ReadOnlyMemory<byte> type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes.AsSpan(), data.Length);
        await output.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(type, cancellationToken).ConfigureAwait(false);

        if (data.Length > 0)
        {
            await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        var crc = ComputeChunkCrc(type.Span, data.Span);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes.AsSpan(), crc);
        await output.WriteAsync(crcBytes, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        output.Write(lengthBytes);
        output.Write(type);
        if (data.Length > 0)
        {
            output.Write(data);
        }

        var crc = ComputeChunkCrc(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    /// <summary>CRC-32/ISO-HDLC over chunk type + data (the PNG chunk checksum).</summary>
    private static uint ComputeChunkCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var total = type.Length + data.Length;
        if (total <= 256)
        {
            Span<byte> joined = stackalloc byte[total];
            type.CopyTo(joined);
            data.CopyTo(joined[type.Length..]);
            return System.IO.Hashing.Crc32.HashToUInt32(joined);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            type.CopyTo(buffer);
            data.CopyTo(buffer.AsSpan(type.Length));
            return System.IO.Hashing.Crc32.HashToUInt32(buffer.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
