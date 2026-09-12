using Microsoft.Win32.SafeHandles;

namespace CrossMacro.Platform.Windows.Native;

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    internal static partial IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial UIntPtr GlobalSize(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;
    public const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

    public const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    public const uint SYNCHRONIZE = 0x00100000;
    public const uint TIMER_QUERY_STATE = 0x00000001;
    public const uint TIMER_MODIFY_STATE = 0x00000002;

    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint INFINITE = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeWaitHandle CreateWaitableTimerEx(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(
        SafeWaitHandle hHandle,
        uint dwMilliseconds);
}
