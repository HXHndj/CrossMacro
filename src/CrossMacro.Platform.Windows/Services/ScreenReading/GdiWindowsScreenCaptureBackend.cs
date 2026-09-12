using System.Buffers;

namespace CrossMacro.Platform.Windows.Services.ScreenReading;

// S6640: the DIB pointer is only readable through unsafe pointer math; the
// copy length is derived from the DIB that owns the pointer and cannot overrun.
#pragma warning disable S6640

internal sealed class GdiWindowsScreenCaptureBackend : IWindowsScreenCaptureBackend, IDisposable
{
    private const ushort BitsPerPixel = 32;

    private readonly Lock _gate = new();
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _bits;
    private IntPtr _previousObject;
    private int _cachedWidth;
    private int _cachedHeight;
    private bool _disposed;

    public ScreenRect GetVirtualScreenBounds()
    {
        var x = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        var y = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        var width = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        var height = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Windows virtual screen dimensions are invalid: {width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}.");
        }

        return new ScreenRect(x, y, width, height);
    }

    public WindowsScreenCaptureFrame Capture(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var screenDc = User32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw CreateWin32Exception("GetDC(NULL) failed");
            }

            try
            {
                // The memory DC and DIB section are the expensive parts of a GDI
                // capture; reuse them across calls and rebuild only when the
                // requested size changes. The output buffer comes from the shared
                // array pool and is returned when the owning frame is disposed.
                EnsureCompatibleResources(screenDc, region.Width, region.Height);

                cancellationToken.ThrowIfCancellationRequested();
                PerformCaptureBlt(screenDc, _memoryDc, region.X, region.Y, region.Width, region.Height);

                var stride = checked(region.Width * ScreenFrame.GetBytesPerPixel(ScreenPixelFormat.Bgra8888));
                var pixelCount = checked(stride * region.Height);
                var buffer = new PooledBufferMemoryManager(pixelCount);
                try
                {
                    unsafe
                    {
                        new ReadOnlySpan<byte>((void*)_bits, pixelCount).CopyTo(buffer.GetSpan());
                    }

                    return new WindowsScreenCaptureFrame(region, stride, ScreenPixelFormat.Bgra8888, buffer.BufferMemory, buffer);
                }
                catch
                {
                    ((IDisposable)buffer).Dispose();
                    throw;
                }
            }
            catch
            {
                // Any failure leaves the cached GDI objects in an unknown state.
                ReleaseCachedResources();
                throw;
            }
            finally
            {
                _ = User32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private void EnsureCompatibleResources(IntPtr screenDc, int width, int height)
    {
        if (_memoryDc != IntPtr.Zero && width == _cachedWidth && height == _cachedHeight)
        {
            return;
        }

        ReleaseCachedResources();
        CreateCaptureResources(screenDc, width, height, out _memoryDc, out _bitmap, out _bits, out _previousObject);
        _cachedWidth = width;
        _cachedHeight = height;
    }

    private static void CreateCaptureResources(
        IntPtr screenDc,
        int width,
        int height,
        out IntPtr memoryDc,
        out IntPtr bitmap,
        out IntPtr bits,
        out IntPtr previousObject)
    {
        memoryDc = Gdi32.CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            throw CreateWin32Exception("CreateCompatibleDC failed");
        }

        var bitmapInfo = CreateBitmapInfo(width, height);
        bitmap = Gdi32.CreateDIBSection(
            screenDc,
            ref bitmapInfo,
            Gdi32.DibRgbColors,
            out bits,
            IntPtr.Zero,
            0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            // No cleanup here: out params alias the caller's locals, so the caller's finally
            // (ReleaseCachedResources) would double-free anything deleted in this method.
            throw CreateWin32Exception("CreateDIBSection failed");
        }

        previousObject = Gdi32.SelectObject(memoryDc, bitmap);
        if (previousObject == IntPtr.Zero || previousObject == Gdi32.HbitmapError)
        {
            throw CreateWin32Exception("SelectObject failed");
        }
    }

    private static void PerformCaptureBlt(IntPtr screenDc, IntPtr memoryDc, int x, int y, int width, int height)
    {
        if (!Gdi32.BitBlt(
                memoryDc,
                0,
                0,
                width,
                height,
                screenDc,
                x,
                y,
                Gdi32.Srccopy | Gdi32.CaptureBlt))
        {
            throw CreateWin32Exception("BitBlt failed");
        }

        if (!Gdi32.GdiFlush())
        {
            throw CreateWin32Exception("GdiFlush failed");
        }
    }

    private void ReleaseCachedResources()
    {
        if (_previousObject != IntPtr.Zero && _previousObject != Gdi32.HbitmapError && _memoryDc != IntPtr.Zero)
        {
            _ = Gdi32.SelectObject(_memoryDc, _previousObject);
        }

        if (_bitmap != IntPtr.Zero)
        {
            _ = Gdi32.DeleteObject(_bitmap);
        }

        if (_memoryDc != IntPtr.Zero)
        {
            _ = Gdi32.DeleteDC(_memoryDc);
        }

        _memoryDc = IntPtr.Zero;
        _bitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _previousObject = IntPtr.Zero;
        _cachedWidth = 0;
        _cachedHeight = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseCachedResources();
        }
    }

#pragma warning restore S6640

    private static BitmapInfo CreateBitmapInfo(int width, int height)
    {
        return new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = checked(-height),
                biPlanes = 1,
                biBitCount = BitsPerPixel,
                biCompression = Gdi32.BiRgb,
                biSizeImage = (uint)checked(width * height * ScreenFrame.GetBytesPerPixel(ScreenPixelFormat.Bgra8888)),
            },
        };
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return error is 0
            ? new Win32Exception($"{operation}.")
            : new Win32Exception(error, $"{operation}: {new Win32Exception(error).Message}");
    }
}
