namespace CrossMacro.Platform.Abstractions;

/// <summary>
/// Wraps an <see cref="ArrayPool{T}"/> rental as a <see cref="MemoryManager{T}"/>
/// so frames can hand out <see cref="Memory{T}"/> views that return the buffer
/// to the pool on <see cref="IDisposable.Dispose"/>. Large frame buffers (a
/// 1080p capture is ~8 MB) otherwise hammer the large object heap on every poll.
/// </summary>
public sealed class PooledBufferMemoryManager : MemoryManager<byte>
{
    private byte[]? _buffer;
    private readonly int _length;
    private readonly bool _clearOnReturn;

    public PooledBufferMemoryManager(int minimumLength, bool clearOnReturn = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        _buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
        _length = minimumLength;
        _clearOnReturn = clearOnReturn;
    }

    /// <summary>The pooled view; exactly <c>minimumLength</c> bytes long.</summary>
    public Memory<byte> BufferMemory => Memory;

    public override Span<byte> GetSpan()
    {
        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledBufferMemoryManager));
        return buffer.AsSpan(0, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledBufferMemoryManager));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
        return buffer.AsMemory(elementIndex, _length - elementIndex).Pin();
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            if (_clearOnReturn)
            {
                Array.Clear(buffer, 0, _length);
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
