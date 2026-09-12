
namespace CrossMacro.Platform.Windows.Services;

/// <summary>
/// Decouples the low-level hook callbacks from the managed processing pipeline.
/// Hook callbacks only enqueue; a dedicated consumer thread raises
/// <see cref="WindowsInputCapture.InputReceived"/> so lock contention, logging,
/// or GC pauses in subscribers can never stall the system-wide input chain
/// (which would risk Windows silently removing the hook after
/// LowLevelHooksTimeout). Mirrors the macOS <c>MacOSInputEventDispatcher</c>.
/// </summary>
internal sealed class WindowsInputEventDispatcher : IDisposable
{
    internal const int DefaultCapacity = 4096;
    internal const int ShutdownWaitMilliseconds = 1000;

    private readonly BlockingCollection<CapturedInputEventArgs> _queue;
    private readonly Action<CapturedInputEventArgs> _dispatch;
    private readonly Action<Exception> _reportError;
    private readonly Thread _thread;
    private int _accepting = 1;
    private int _stopDispatching;
    private int _completed;

    public WindowsInputEventDispatcher(
        Action<CapturedInputEventArgs> dispatch,
        Action<Exception> reportError,
        int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(reportError);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _dispatch = dispatch;
        _reportError = reportError;
        _queue = new BlockingCollection<CapturedInputEventArgs>(
            new ConcurrentQueue<CapturedInputEventArgs>(),
            capacity);
        _thread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "WindowsInputDispatch",
        };
        _thread.Start();
    }

    public bool IsCompleted => Volatile.Read(ref _completed) is not 0;

    public bool TryEnqueue(CapturedInputEventArgs inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        if (Volatile.Read(ref _accepting) is 0)
        {
            return false;
        }

        try
        {
            return _queue.TryAdd(inputEvent);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _accepting, 0) is not 0)
        {
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                // The dispatch thread already drained and released the queue.
            }
        }

        if (!ReferenceEquals(Thread.CurrentThread, _thread)
            && _thread.IsAlive
            && !_thread.Join(ShutdownWaitMilliseconds))
        {
            // A managed subscriber may still be executing. There is no safe
            // way to abort that delegate. Stop dispatching any remaining queued
            // input once it returns, but leave the queue owned by the worker so
            // it can finish and clean up asynchronously.
            Volatile.Write(ref _stopDispatching, 1);
            var droppedCount = 0;
            try
            {
                while (_queue.TryTake(out _))
                {
                    droppedCount++;
                }
            }
            catch (ObjectDisposedException)
            {
                // The worker finished between Join and the queue drain.
            }

            Debug.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[WindowsInputCapture] Input dispatch worker did not quiesce within {ShutdownWaitMilliseconds} ms; dropped {droppedCount} queued input event(s), and the worker retains queue ownership."));
        }

        GC.SuppressFinalize(this);
    }

    private void DispatchLoop()
    {
        try
        {
            foreach (CapturedInputEventArgs inputEvent in _queue.GetConsumingEnumerable(CancellationToken.None))
            {
                if (Volatile.Read(ref _stopDispatching) is not 0)
                {
                    break;
                }

                try
                {
                    _dispatch(inputEvent);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ReportError(ex);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _completed, 1);
            _queue.Dispose();
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _reportError(exception);
        }
        catch (Exception errorHandlerException) when (errorHandlerException is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WindowsInputCapture] Error handler threw: {errorHandlerException}");
        }
    }
}
