
namespace CrossMacro.Platform.Windows.Services;

public sealed class WindowsInputCapture : IInputCapture, IMouseCoordinateModeInputCapture
{
    private const uint NotifyForThisSession = 0;
    private static readonly IntPtr HwndMessage = new(-3);

    public string ProviderName => "Windows Hooks";
    public bool IsSupported => OperatingSystem.IsWindows();

    public event EventHandler<CapturedInputEventArgs>? InputReceived;
    public event EventHandler<InputCaptureErrorEventArgs>? CaptureError;

    private bool _captureMouse;
    private bool _captureKeyboard;
    private bool _useAbsoluteCoordinates;
    private bool _useRawRelativeCoordinates;

    private int _lastX;
    private int _lastY;
    private bool _firstMove = true;

    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private IntPtr _sessionWindowHandle = IntPtr.Zero;
    private User32.HookProc? _mouseProc;
    private User32.HookProc? _keyboardProc;
    private User32.WindowProc? _sessionWindowProc;
    private readonly string _sessionWindowClassName = $"CrossMacroSessionSwitch_{Guid.NewGuid():N}";
    private bool _sessionNotificationRegistered;
    private bool _rawMouseInputRegistered;
    private IntPtr _rawInputBuffer;
    private int _rawInputBufferSize;

    private uint _messagePumpThreadId;
    private CancellationTokenRegistration _startCancellationRegistration;
    private readonly IWindowsHookInstaller _hookInstaller;
    private WindowsInputEventDispatcher? _inputDispatcher;
    private WindowsInputEventDispatcher? _retiredDispatcher;
    private readonly Lock _lifecycleLock = new();
    private Thread? _messagePumpThread;
    private int _disposed;
    private int _stopRequested;
    private int _dispatchOverflowSignaled;
    private readonly Func<ThreadStart, Thread> _threadFactory;

    public WindowsInputCapture()
        : this(new DefaultWindowsHookInstaller(), static start => new Thread(start) { IsBackground = true }) { /* Empty */ }

    internal WindowsInputCapture(IWindowsHookInstaller hookInstaller)
        : this(hookInstaller, static start => new Thread(start) { IsBackground = true }) { /* Empty */ }

    internal WindowsInputCapture(
        IWindowsHookInstaller hookInstaller,
        Func<ThreadStart, Thread> threadFactory)
    {
        ArgumentNullException.ThrowIfNull(hookInstaller);
        ArgumentNullException.ThrowIfNull(threadFactory);
        _hookInstaller = hookInstaller;
        _threadFactory = threadFactory;
    }

    public void Configure(bool captureMouse, bool captureKeyboard)
    {
        _captureMouse = captureMouse;
        _captureKeyboard = captureKeyboard;
    }

    public void ConfigureCoordinateMode(
        bool useAbsoluteCoordinates,
        bool useLogicalCoordinates)
    {
        _useAbsoluteCoordinates = useAbsoluteCoordinates;
        _useRawRelativeCoordinates = !useAbsoluteCoordinates && !useLogicalCoordinates;
    }


    public async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startupTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread messagePumpThread;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed is not 0, this);
            if (_messagePumpThread is not null
                || Volatile.Read(ref _inputDispatcher) is not null
                || (_retiredDispatcher is { } retiredDispatcher && !retiredDispatcher.IsCompleted))
            {
                throw new InvalidOperationException(
                    "Windows input capture is already running or its previous dispatch worker is still stopping.");
            }

            // A completed retired worker is safe to forget before creating the
            // next generation. An incomplete worker remains rooted here so an
            // old callback cannot overlap a new dispatcher on this instance.
            _retiredDispatcher = null;
            Volatile.Write(ref _stopRequested, 0);
            messagePumpThread = _threadFactory(() => RunMessagePumpThread(startupTcs, ct));
            _messagePumpThread = messagePumpThread;

            try
            {
                // Start while the reservation is still protected. A second
                // StartAsync can therefore never observe a non-null but not
                // yet started thread as available.
                messagePumpThread.Start();
            }
            catch
            {
                if (ReferenceEquals(_messagePumpThread, messagePumpThread))
                {
                    _messagePumpThread = null;
                }

                throw;
            }
        }

        await _startCancellationRegistration.DisposeAsync().ConfigureAwait(false);
        _startCancellationRegistration = ct.Register(() =>
        {
            _ = startupTcs.TrySetCanceled(ct);
            StopCapture();
        });

        await startupTcs.Task.ConfigureAwait(false);
    }

    private void RunMessagePumpThread(TaskCompletionSource startupTcs, CancellationToken ct)
    {
        try
        {
            _messagePumpThreadId = Kernel32.GetCurrentThreadId();

            // The dispatcher must exist before the hooks are installed so the
            // callbacks can always enqueue. Managed processing then happens on
            // the dedicated dispatch thread, never inside the hook callback.
            Volatile.Write(
                ref _inputDispatcher,
                new WindowsInputEventDispatcher(DispatchInputEvent, ReportDispatchError));
            _dispatchOverflowSignaled = 0;

            _mouseProc = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;
            _sessionWindowProc = SessionWindowCallback;

            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                IntPtr moduleHandle = Kernel32.GetModuleHandle(curModule?.ModuleName);
                InstallConfiguredHooks(moduleHandle);
            }

            RegisterSessionNotificationWindow();
            _ = startupTcs.TrySetResult();
            if (Volatile.Read(ref _stopRequested) is not 0)
            {
                // StopCapture can race thread initialization before a native
                // message queue exists. Re-issue the quit after initialization
                // so that early stop requests cannot strand this thread.
                WindowsMessagePump.RequestStop(_messagePumpThreadId);
            }
            RunWindowsMessageLoop(ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is OutOfMemoryException)
            {
                throw;
            }

            if (!startupTcs.TrySetException(ex) && !startupTcs.Task.IsCanceled)
            {
                CaptureError?.Invoke(this, new InputCaptureErrorEventArgs($"Message pump error: {ex.Message}"));
            }
        }
        finally
        {
            UnregisterSessionNotificationWindow();
            UninstallHooks();
            // Hooks are gone, so no more enqueues can race the drain. Keep a
            // reference to an incomplete worker until it reports completion;
            // this prevents a same-instance restart from overlapping its old
            // queued callbacks with a new dispatcher.
            var dispatcher = Volatile.Read(ref _inputDispatcher);
            if (dispatcher is not null)
            {
                Volatile.Write(ref _retiredDispatcher, dispatcher);
                dispatcher.Dispose();
                Volatile.Write(ref _inputDispatcher, null);
            }
            ReleaseRawInputBuffer();
            _messagePumpThreadId = 0;
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_messagePumpThread, Thread.CurrentThread))
                {
                    _messagePumpThread = null;
                }
            }
        }
    }

    private void InstallConfiguredHooks(IntPtr moduleHandle)
    {
        if (_captureMouse)
        {
            InitializeMousePosition();
            _mouseHookHandle = InstallMouseHook(moduleHandle);
            if (_mouseHookHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to install mouse hook");
            }
        }

        if (_captureKeyboard)
        {
            _keyboardHookHandle = InstallKeyboardHook(moduleHandle);
            if (_keyboardHookHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to install keyboard hook");
            }
        }
    }

    private void InitializeMousePosition()
    {
        _firstMove = !User32.GetCursorPos(out var position);
        if (!_firstMove)
        {
            _lastX = position.x;
            _lastY = position.y;
        }
    }

    private static void RunWindowsMessageLoop(CancellationToken ct)
        => WindowsMessagePump.Run(ct);

    public void StopCapture()
    {
        Volatile.Write(ref _stopRequested, 1);
        _startCancellationRegistration.Dispose();

        if (_messagePumpThreadId != 0)
        {
            WindowsMessagePump.RequestStop(_messagePumpThreadId);
        }
    }

    private IntPtr InstallMouseHook(IntPtr moduleHandle)
        => _hookInstaller.InstallMouseHook(moduleHandle, _mouseProc!);

    private IntPtr InstallKeyboardHook(IntPtr moduleHandle)
        => _hookInstaller.InstallKeyboardHook(moduleHandle, _keyboardProc!);

    private void RegisterSessionNotificationWindow()
    {
        var instanceHandle = Kernel32.GetModuleHandle(lpModuleName: null);
        var classNamePointer = Marshal.StringToCoTaskMemUni(_sessionWindowClassName);
        var windowProcPointer = Marshal.GetFunctionPointerForDelegate(_sessionWindowProc!);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = windowProcPointer,
            hInstance = instanceHandle,
            lpszClassName = classNamePointer,
        };

        try
        {
            if (User32.RegisterClassEx(ref windowClass) is 0)
            {
                ThrowIfRawInputRequired("register the raw input window class");
                Log.Warning("[WindowsInputCapture] Failed to register session notification window class");
                return;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(classNamePointer);
        }

        _sessionWindowHandle = User32.CreateWindowEx(
            0,
            _sessionWindowClassName,
            _sessionWindowClassName,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            instanceHandle,
            IntPtr.Zero);

        if (_sessionWindowHandle == IntPtr.Zero)
        {
            ThrowIfRawInputRequired("create the raw input window");
            Log.Warning("[WindowsInputCapture] Failed to create session notification window");
            _ = User32.UnregisterClass(_sessionWindowClassName, instanceHandle);
            return;
        }

        RegisterRawMouseInput();

        if (!WtsApi32.WTSRegisterSessionNotification(_sessionWindowHandle, NotifyForThisSession))
        {
            Log.Warning("[WindowsInputCapture] Failed to register session notifications");
            return;
        }

        _sessionNotificationRegistered = true;
    }

    private void UnregisterSessionNotificationWindow()
    {
        DestroySessionNotificationWindow(Kernel32.GetModuleHandle(lpModuleName: null));
    }

    private void DestroySessionNotificationWindow(IntPtr instanceHandle)
    {
        // Raw input registration is process-global. Do not remove it here: an older
        // capture can otherwise unregister a newer capture during a rapid restart.
        if (_sessionNotificationRegistered && _sessionWindowHandle != IntPtr.Zero)
        {
            _ = WtsApi32.WTSUnRegisterSessionNotification(_sessionWindowHandle);
            _sessionNotificationRegistered = false;
        }

        if (_sessionWindowHandle != IntPtr.Zero)
        {
            _ = User32.DestroyWindow(_sessionWindowHandle);
            _sessionWindowHandle = IntPtr.Zero;
        }

        _ = User32.UnregisterClass(_sessionWindowClassName, instanceHandle);
    }

    private IntPtr SessionWindowCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == User32.WM_INPUT && _rawMouseInputRegistered)
        {
            ProcessRawMouseInput(lParam);
        }
        else if (IsSessionRecoveryMessage(msg, wParam))
        {
            CaptureError?.Invoke(this, new InputCaptureErrorEventArgs("Recovery: Windows session unlocked; restarting input capture."));
        }

        return User32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void UninstallHooks()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            _ = User32.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            _ = User32.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is not 0)
        {
            return;
        }

        StopCapture();
    }

    /// <summary>
    /// Called on the hook thread only: enqueue and return immediately so the
    /// system-wide input chain is never blocked by managed subscribers.
    /// </summary>
    private void EnqueueInput(CapturedInputEventArgs args)
    {
        var dispatcher = Volatile.Read(ref _inputDispatcher);
        if (dispatcher is not null && !dispatcher.TryEnqueue(args))
        {
            HandleDispatchOverflow();
        }
    }

    internal void HandleDispatchOverflow()
    {
        if (Interlocked.Exchange(ref _dispatchOverflowSignaled, 1) is not 0)
        {
            return;
        }

        var message =
            $"The Windows input event queue exceeded {WindowsInputEventDispatcher.DefaultCapacity} events; capture was stopped because the dispatch thread could not keep up.";
        StopCapture();
        QueueCaptureError(message);
    }

    private void QueueCaptureError(string message)
    {
        try
        {
            if (!ThreadPool.QueueUserWorkItem(
                    static state =>
                    {
                        var notification = (CaptureErrorNotification)state!;
                        notification.Capture.ReportCaptureError(notification.Message);
                    },
                    new CaptureErrorNotification(this, message)))
            {
                Debug.WriteLine("[WindowsInputCapture] Failed to queue capture error notification.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[WindowsInputCapture] Failed to queue capture error notification: {ex}");
        }
    }

    private void ReportCaptureError(string message)
    {
        try
        {
            CaptureError?.Invoke(this, new InputCaptureErrorEventArgs(message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[WindowsInputCapture] Capture error subscriber threw");
        }
    }

    private sealed record CaptureErrorNotification(WindowsInputCapture Capture, string Message);

    private void DispatchInputEvent(CapturedInputEventArgs args) => InputReceived?.Invoke(this, args);

    private void ReportDispatchError(Exception exception) =>
        CaptureError?.Invoke(this, new InputCaptureErrorEventArgs($"Input dispatch error: {exception.Message}"));

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            uint msg = (uint)wParam;

            if (msg == User32.WM_MOUSEMOVE && !_useRawRelativeCoordinates)
            {
                HandleMouseMove(hookStruct.pt.x, hookStruct.pt.y);
            }
            else if (TryMapMouseButtonOrScroll(msg, hookStruct.mouseData, out ushort evdevCode, out int value, out ushort type))
            {
                EmitMouseButtonOrScrollEvent(evdevCode, value, type);
            }
        }
        return User32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    internal static bool TryMapMouseButtonOrScroll(uint msg, uint mouseData, out ushort evdevCode, out int value, out ushort type)
        => WindowsInputEventPolicy.TryMapMouseButtonOrScroll(msg, mouseData, out evdevCode, out value, out type);

    private void HandleMouseMove(int currentX, int currentY)
    {
        bool hadPreviousPosition = !_firstMove;
        int previousX = _lastX;
        int previousY = _lastY;
        if (_firstMove)
        {
            _lastX = currentX;
            _lastY = currentY;
            _firstMove = false;

            if (!_useAbsoluteCoordinates)
            {
                return;
            }
        }

        if (hadPreviousPosition && currentX == previousX && currentY == previousY)
        {
            return;
        }

        var movement = ResolveMouseMovement(
            _useAbsoluteCoordinates,
            currentX,
            currentY,
            previousX,
            previousY);

        _lastX = currentX;
        _lastY = currentY;
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long timestampMicroseconds = GetMonotonicTimestampMicroseconds();

        // The hook already carries both axes; emit one MouseMove2D event instead
        // of separate X/Y/Sync events (3x pipeline cost per physical move).
        var movementArgs = new CapturedInputEvent
        {
            Type = InputEventType.MouseMove2D,
            Code = movement.XCode,
            Value = movement.XValue,
            ValueY = movement.YValue,
            Timestamp = timestamp,
            TimestampMicroseconds = timestampMicroseconds,
            DeviceName = "VirtualMouse",
        };
        EnqueueInput(new CapturedInputEventArgs(movementArgs));
    }

    internal static (ushort XCode, int XValue, ushort YCode, int YValue) ResolveMouseMovement(
        bool useAbsoluteCoordinates,
        int currentX,
        int currentY,
        int previousX,
        int previousY) => WindowsInputEventPolicy.ResolveMouseMovement(
            useAbsoluteCoordinates,
            currentX,
            currentY,
            previousX,
            previousY);

    internal static bool TryResolveRawRelativeMovement(
        ushort flags,
        int deltaX,
        int deltaY,
        out int rawDeltaX,
        out int rawDeltaY)
    {
        rawDeltaX = 0;
        rawDeltaY = 0;
        if ((flags & User32.MouseMoveAbsolute) is not 0 || (deltaX is 0 && deltaY is 0))
        {
            return false;
        }

        rawDeltaX = deltaX;
        rawDeltaY = deltaY;
        return true;
    }

    private void RegisterRawMouseInput()
    {
        _rawMouseInputRegistered = false;
        if (!_captureMouse || !_useRawRelativeCoordinates)
        {
            return;
        }

        var rawMouseDevice = new RawInputDevice
        {
            UsagePage = User32.HidUsagePageGeneric,
            Usage = User32.HidUsageGenericMouse,
            Flags = User32.RidevInputSink,
            TargetWindow = _sessionWindowHandle,
        };
        if (!User32.RegisterRawInputDevices(
                in rawMouseDevice,
                numberOfDevices: 1,
                sizeOfRawInputDevice: (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to register raw mouse input");
        }

        _rawMouseInputRegistered = true;
    }

    private void ThrowIfRawInputRequired(string operation)
    {
        if (_captureMouse && _useRawRelativeCoordinates)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Failed to {operation} for raw mouse input");
        }
    }

    private void ProcessRawMouseInput(IntPtr rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (User32.GetRawInputData(rawInputHandle, User32.RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue
            || size < headerSize + (uint)Marshal.SizeOf<RawMouse>())
        {
            return;
        }

        EnsureRawInputBufferCapacity(checked((int)size));
        uint bytesRead = User32.GetRawInputData(rawInputHandle, User32.RidInput, _rawInputBuffer, ref size, headerSize);
        if (bytesRead == uint.MaxValue || bytesRead < headerSize + (uint)Marshal.SizeOf<RawMouse>())
        {
            return;
        }

        var header = Marshal.PtrToStructure<RawInputHeader>(_rawInputBuffer);
        if (header.Type is not User32.RimTypeMouse)
        {
            return;
        }

        var mouse = Marshal.PtrToStructure<RawMouse>(IntPtr.Add(_rawInputBuffer, (int)headerSize));
        if (TryResolveRawRelativeMovement(mouse.Flags, mouse.LastX, mouse.LastY, out int deltaX, out int deltaY))
        {
            EmitRawMouseMovement(deltaX, deltaY);
        }
    }

    private void EnsureRawInputBufferCapacity(int requiredSize)
    {
        if (_rawInputBufferSize >= requiredSize)
        {
            return;
        }

        _rawInputBuffer = Marshal.ReAllocHGlobal(_rawInputBuffer, (IntPtr)requiredSize);
        _rawInputBufferSize = requiredSize;
    }

    private void ReleaseRawInputBuffer()
    {
        if (_rawInputBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = IntPtr.Zero;
            _rawInputBufferSize = 0;
        }

        _rawMouseInputRegistered = false;
    }

    private void EmitRawMouseMovement(int deltaX, int deltaY)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long timestampMicroseconds = GetMonotonicTimestampMicroseconds();

        // Raw input carries both axes per report; emit one MouseMove2D event.
        EnqueueInput(new CapturedInputEventArgs(new CapturedInputEvent
        {
            Type = InputEventType.MouseMove2D,
            Code = InputEventCode.REL_X,
            Value = deltaX,
            ValueY = deltaY,
            Timestamp = timestamp,
            TimestampMicroseconds = timestampMicroseconds,
            DeviceName = "RawMouse",
        }));
    }

    private void EmitMouseButtonOrScrollEvent(ushort evdevCode, int value, ushort type)
    {
        InputEventType eventType;
        if (type == InputEventCode.EV_KEY && evdevCode >= 272 && evdevCode <= 279)
        {
            eventType = InputEventType.MouseButton;
        }
        else if (type == InputEventCode.EV_REL && evdevCode is InputEventCode.REL_WHEEL or InputEventCode.REL_HWHEEL)
        {
            eventType = InputEventType.MouseScroll;
        }
        else
        {
            eventType = (InputEventType)type;
        }

        var args = new CapturedInputEvent
        {
            Type = eventType,
            Code = evdevCode,
            Value = value,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimestampMicroseconds = GetMonotonicTimestampMicroseconds(),
            DeviceName = "VirtualMouse",
        };
        EnqueueInput(new CapturedInputEventArgs(args));
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
            uint msg = (uint)wParam;

            if (!ShouldIgnoreKeyboardHookEvent(hookStruct.flags, hookStruct.dwExtraInfo))
            {
                HandleKeyboardEvent(msg, hookStruct);
            }
        }
        return User32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void HandleKeyboardEvent(uint msg, KbdllHookStruct hookStruct)
    {
        bool isDown = msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        bool isUp = msg is User32.WM_KEYUP or User32.WM_SYSKEYUP;

        if (!isDown && !isUp)
        {
            return;
        }

        int evdevCode = MapKeyboardEvent((ushort)hookStruct.vkCode, hookStruct.flags);

        // Debug logging for key analysis
        if (isDown && Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
        {
            Log.Debug("[WindowsInputCapture] KeyDown: VK={VK} (0x{VKHex}), Scan={Scan}, Flags={Flags}, Mapped={Evdev}",
                hookStruct.vkCode, hookStruct.vkCode.ToString("X", CultureInfo.InvariantCulture), hookStruct.scanCode, hookStruct.flags, evdevCode);
        }

        if (evdevCode is not 0)
        {
            var args = new CapturedInputEvent
            {
                Type = (InputEventType)InputEventCode.EV_KEY,
                Code = (ushort)evdevCode,
                Value = isDown ? 1 : 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TimestampMicroseconds = GetMonotonicTimestampMicroseconds(),
                DeviceName = "VirtualKeyboard",
            };
            EnqueueInput(new CapturedInputEventArgs(args));
        }
        else if (isDown)
        {
            Log.Warning("[WindowsInputCapture] Unmapped key: VK={VK} (0x{VKHex})", hookStruct.vkCode, hookStruct.vkCode.ToString("X", CultureInfo.InvariantCulture));
        }
    }

    internal static int MapKeyboardEvent(ushort virtualKey, uint hookFlags)
        => WindowsInputEventPolicy.MapKeyboardEvent(virtualKey, hookFlags);

    internal static bool ShouldIgnoreKeyboardHookEvent(uint hookFlags, IntPtr extraInfo)
        => WindowsInputEventPolicy.ShouldIgnoreKeyboardHookEvent(hookFlags, extraInfo);

    internal static bool IsSessionRecoveryMessage(uint message, IntPtr wParam)
        => WindowsInputEventPolicy.IsSessionRecoveryMessage(message, wParam);

    internal static long GetMonotonicTimestampMicroseconds() =>
        ToMicroseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal static long ToMicroseconds(long timestamp, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequency, 0);
        return checked(
            (timestamp / frequency * 1_000_000L)
            + (timestamp % frequency * 1_000_000L / frequency));
    }
}
