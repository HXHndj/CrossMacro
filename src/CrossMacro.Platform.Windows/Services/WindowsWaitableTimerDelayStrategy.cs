using CrossMacro.Platform.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace CrossMacro.Platform.Windows.Services;

/// <summary>
/// Coarse delay strategy backed by a high-resolution waitable timer
/// (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Windows 10 1803+).
/// Unlike <c>Task.Delay</c>, waits are not quantized to the ~15.6 ms system
/// timer period, so playback deadlines can be approached to sub-millisecond
/// accuracy before the caller's final spin. Falls back to a standard
/// waitable timer and then to <see cref="Task.Delay"/> when the flag or the
/// API is unavailable.
/// </summary>
internal sealed class WindowsWaitableTimerDelayStrategy : ICoarseDelayStrategy, IDisposable
{
    private const int WaitSlackMilliseconds = 100;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeWaitHandle? _timer;
    private int _disposed;
    private readonly Action? _beforeWait;

    public WindowsWaitableTimerDelayStrategy()
        : this(beforeWait: null) { /* Empty */ }

    internal WindowsWaitableTimerDelayStrategy(Action? beforeWait)
    {
        _beforeWait = beforeWait;
        var desiredAccess = Kernel32.SYNCHRONIZE | Kernel32.TIMER_QUERY_STATE | Kernel32.TIMER_MODIFY_STATE;
        var timer = Kernel32.CreateWaitableTimerEx(
            lpTimerAttributes: IntPtr.Zero,
            lpTimerName: null,
            Kernel32.CREATE_WAITABLE_TIMER_MANUAL_RESET | Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            dwDesiredAccess: desiredAccess);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = Kernel32.CreateWaitableTimerEx(
                lpTimerAttributes: IntPtr.Zero,
                lpTimerName: null,
                Kernel32.CREATE_WAITABLE_TIMER_MANUAL_RESET,
                dwDesiredAccess: desiredAccess);
        }

        _timer = timer.IsInvalid ? null : timer;
    }

    public bool IsHighResolution => _timer is not null;

    public async ValueTask WaitAsync(int millisecondsDelay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(millisecondsDelay, 0);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);

        var timer = _timer;
        if (timer is null)
        {
            _beforeWait?.Invoke();
            await Task.Delay(millisecondsDelay, cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            // Negative due time = relative to now, in 100 ns units. Re-arming a
            // manual-reset timer also clears its signaled state.
            var dueTime = -(long)millisecondsDelay * 10_000L;
            if (!Kernel32.SetWaitableTimer(
                    timer,
                    in dueTime,
                    lPeriod: 0,
                    pfnCompletionRoutine: IntPtr.Zero,
                    lpArgToCompletionRoutine: IntPtr.Zero,
                    fResume: false))
            {
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }

            var waitTimeout = checked((uint)(millisecondsDelay + WaitSlackMilliseconds));
            uint waitResult;
            if (!cancellationToken.CanBeCanceled)
            {
                _beforeWait?.Invoke();
                waitResult = Kernel32.WaitForSingleObject(timer, waitTimeout);
            }
            else
            {
                // WaitForSingleObject cannot observe a managed cancellation token.
                // Wait on a short-lived cancellation event beside the timer so a
                // stop request wakes the native wait immediately.
                using var cancellationEvent = new ManualResetEvent(initialState: false);
                using var cancellationRegistration = cancellationToken.Register(
                    static state => _ = ((ManualResetEvent)state!).Set(),
                    cancellationEvent);
#pragma warning disable S3869 // Raw handles are passed only while _gate owns the timer; using + registration keep the cancellation event alive through the wait.
                var handles = new[]
                {
                    timer.DangerousGetHandle(),
                    cancellationEvent.SafeWaitHandle.DangerousGetHandle(),
                };
#pragma warning restore S3869
                _beforeWait?.Invoke();
                waitResult = Kernel32.WaitForMultipleObjects(
                    (uint)handles.Length,
                    handles,
                    bWaitAll: false,
                    waitTimeout);
                if (waitResult == Kernel32.WAIT_OBJECT_0 + 1)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            if (waitResult is not (Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_TIMEOUT))
            {
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is not 0)
        {
            return;
        }

        // The timer handle is only closed after an in-flight wait leaves the
        // gate. The semaphore itself remains usable for a waiter that raced
        // with Dispose; that waiter observes _disposed and exits safely.
        _gate.Wait();
        try
        {
            _timer?.Dispose();
            _timer = null;
        }
        finally
        {
            _ = _gate.Release();
        }
    }
}
