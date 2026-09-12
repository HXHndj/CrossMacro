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

    public WindowsWaitableTimerDelayStrategy()
    {
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

        var timer = _timer;
        if (timer is null)
        {
            await Task.Delay(millisecondsDelay, cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            var waitResult = Kernel32.WaitForSingleObject(
                hHandle: timer,
                dwMilliseconds: unchecked((uint)(millisecondsDelay + WaitSlackMilliseconds)));
            if (waitResult is not (Kernel32.WAIT_OBJECT_0 or Kernel32.WAIT_TIMEOUT))
            {
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _gate.Dispose();
    }
}
