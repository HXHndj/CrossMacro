
namespace CrossMacro.Infrastructure.Services;

/// <summary>
/// Owns one physical <see cref="IInputCapture"/> and hands out lightweight
/// session views over it. Each consumer (global hotkeys, text expansion,
/// recording) keeps its own session lifecycle, while the process installs a
/// single low-level hook chain instead of one chain per consumer. The physical
/// capture is (re)started whenever the union of active session configurations
/// changes and stopped when the last session goes away; session events fan out
/// from the physical capture with the session itself as the sender so
/// consumer-side "is current capture" checks keep working.
/// </summary>
internal sealed class InputCaptureSessionCoordinator : IDisposable
{
    private readonly InputCaptureSessionFactory _physicalFactory;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly List<InputCaptureSession> _sessions = [];

    private IInputCapture? _physical;
    private CancellationTokenSource? _physicalCts;
    private Task? _physicalStartTask;
    private (bool Mouse, bool Keyboard) _runningConfig;
    private (bool Absolute, bool Logical)? _runningMode;
    private bool _physicalUnhealthy;
    private int _generation;
    private bool _disposed;
    private int _recomputationQueued;

    public InputCaptureSessionCoordinator(InputCaptureSessionFactory physicalFactory)
    {
        _physicalFactory = physicalFactory ?? throw new ArgumentNullException(nameof(physicalFactory));
    }

    public IInputCapture CreateSession()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = new InputCaptureSession(this);
            _sessions.Add(session);
            return session;
        }
    }

    public void Dispose()
    {
        List<InputCaptureSession> sessions;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _sessions];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Deactivate();
        }

        // Best-effort synchronous stop; queued recomputations no-op once disposed.
        StopPhysicalAsync().GetAwaiter().GetResult();
        _stateGate.Dispose();
    }

    internal string ProviderName => _physical?.ProviderName ?? "Shared input capture";

    private void RemoveSession(InputCaptureSession session)
    {
        lock (_lock)
        {
            _ = _sessions.Remove(session);
        }
    }

    /// <summary>Coalesced, serialized convergence towards the desired capture state.</summary>
    private void QueueRecompute()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _recomputationQueued, 1) is not 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ConvergeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
            {
                Log.LogError(ex, "[InputCaptureSessionCoordinator] Background capture convergence failed");
            }
            finally
            {
                _ = Interlocked.Exchange(ref _recomputationQueued, 0);
            }
        });
    }

    private async Task ConvergeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            while (true)
            {
                if (_disposed)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var desired = ComputeDesiredState();
                if (MatchesRunningState(desired))
                {
                    return;
                }

                if (desired is null)
                {
                    await StopPhysicalAsync().ConfigureAwait(false);
                    return;
                }

                await StopPhysicalAsync().ConfigureAwait(false);
                await StartPhysicalAsync(desired, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private sealed record DesiredState(bool Mouse, bool Keyboard, (bool Absolute, bool Logical)? Mode);

    private DesiredState? ComputeDesiredState()
    {
        lock (_lock)
        {
            var mouse = false;
            var keyboard = false;
            (bool Absolute, bool Logical)? mode = null;
            foreach (var session in _sessions)
            {
                if (!session.IsActive)
                {
                    continue;
                }

                mouse |= session.CaptureMouse;
                keyboard |= session.CaptureKeyboard;
                if (session.HasCoordinateMode)
                {
                    mode = (session.UseAbsoluteCoordinates, session.UseLogicalCoordinates);
                }
            }

            if (!mouse && !keyboard)
            {
                return null;
            }

            return new DesiredState(mouse, keyboard, mode);
        }
    }

    private bool MatchesRunningState(DesiredState? desired)
    {
        lock (_lock)
        {
            if (_physical is null || _physicalUnhealthy || desired is null)
            {
                return false;
            }

            return _runningConfig == (desired.Mouse, desired.Keyboard)
                && _runningMode == desired.Mode;
        }
    }

    private async Task StopPhysicalAsync()
    {
        IInputCapture? physical;
        CancellationTokenSource? cts;
        Task? startTask;
        lock (_lock)
        {
            physical = _physical;
            cts = _physicalCts;
            startTask = _physicalStartTask;
            _physical = null;
            _physicalCts = null;
            _physicalStartTask = null;
            _physicalUnhealthy = false;
            _runningConfig = default;
            _runningMode = null;
            _generation++;
        }

        if (physical is null)
        {
            return;
        }

        physical.InputReceived -= OnPhysicalInputReceived;
        physical.CaptureError -= OnPhysicalCaptureError;

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                // Already cancelled or disposed; cleanup proceeds regardless.
            }
        }

        try
        {
            physical.StopCapture();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[InputCaptureSessionCoordinator] Error stopping shared input capture");
        }

        physical.Dispose();

        if (startTask is not null)
        {
            try
            {
                await startTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Startup faults are reported to the awaiting session, if any.
                Log.Debug(ex, "[InputCaptureSessionCoordinator] Shared capture start task ended with an error during shutdown");
            }
        }

        cts?.Dispose();
    }

    private async Task StartPhysicalAsync(DesiredState desired, CancellationToken cancellationToken)
    {
        var physical = _physicalFactory();
        physical.Configure(desired.Mouse, desired.Keyboard);
        if (desired.Mode is { } mode && physical is IMouseCoordinateModeInputCapture modeAware)
        {
            modeAware.ConfigureCoordinateMode(mode.Absolute, mode.Logical);
        }

        physical.InputReceived += OnPhysicalInputReceived;
        physical.CaptureError += OnPhysicalCaptureError;

        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _physical = physical;
            _physicalCts = cts;
            _physicalUnhealthy = false;
            _runningConfig = (desired.Mouse, desired.Keyboard);
            _runningMode = desired.Mode;
        }

        try
        {
            _physicalStartTask = physical.StartAsync(cancellationToken);
            await _physicalStartTask.ConfigureAwait(false);
        }
        catch
        {
            // Roll back so the next convergence attempt retries cleanly.
            await StopPhysicalAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnPhysicalInputReceived(object? sender, CapturedInputEventArgs args)
    {
        InputCaptureSession[] snapshot;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            snapshot = [.. _sessions];
        }

        foreach (var session in snapshot)
        {
            if (!session.IsActive)
            {
                continue;
            }

            try
            {
                session.RaiseInputReceived(args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogError(ex, "[InputCaptureSessionCoordinator] Input subscriber threw");
            }
        }
    }

    private void OnPhysicalCaptureError(object? sender, InputCaptureErrorEventArgs args)
    {
        InputCaptureSession[] snapshot;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _physicalUnhealthy = true;
            snapshot = [.. _sessions];
        }

        foreach (var session in snapshot)
        {
            if (!session.IsActive)
            {
                continue;
            }

            try
            {
                session.RaiseCaptureError(args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogError(ex, "[InputCaptureSessionCoordinator] Error subscriber threw");
            }
        }

        // Consumer-side restarts only recycle sessions; converge the physical
        // capture too so a dead backend is actually recreated.
        QueueRecompute();
    }

    private async Task StartSessionAsync(InputCaptureSession session, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session.IsActive = true;
        }

        await ConvergeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StopSession(InputCaptureSession session)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            session.IsActive = false;
        }

        QueueRecompute();
    }

    private void OnSessionConfigured()
    {
        QueueRecompute();
    }

    private void EnsureExclusiveCoordinateMode(InputCaptureSession session)
    {
        lock (_lock)
        {
            foreach (var other in _sessions)
            {
                if (!ReferenceEquals(other, session) && other.HasCoordinateMode)
                {
                    throw new InvalidOperationException(
                        "Another recording session already owns the shared input capture's coordinate mode.");
                }
            }
        }
    }

    /// <summary>Lightweight per-consumer view over the shared physical capture.</summary>
    private sealed class InputCaptureSession : IInputCapture, IMouseCoordinateModeInputCapture
    {
        private readonly InputCaptureSessionCoordinator _owner;

        public InputCaptureSession(InputCaptureSessionCoordinator owner)
        {
            _owner = owner;
        }

        public event EventHandler<CapturedInputEventArgs>? InputReceived;
        public event EventHandler<InputCaptureErrorEventArgs>? CaptureError;

        public string ProviderName => _owner.ProviderName;

        /// <summary>Delegated to the physical capture once it exists; sessions themselves are always constructible.</summary>
        public bool IsSupported => true;

        internal bool CaptureMouse { get; private set; }
        internal bool CaptureKeyboard { get; private set; }
        internal bool HasCoordinateMode { get; private set; }
        internal bool UseAbsoluteCoordinates { get; private set; }
        internal bool UseLogicalCoordinates { get; private set; }
        internal volatile bool IsActive;

        public void Configure(bool captureMouse, bool captureKeyboard)
        {
            CaptureMouse = captureMouse;
            CaptureKeyboard = captureKeyboard;
            if (IsActive)
            {
                _owner.OnSessionConfigured();
            }
        }

        public void ConfigureCoordinateMode(bool useAbsoluteCoordinates, bool useLogicalCoordinates)
        {
            _owner.EnsureExclusiveCoordinateMode(this);
            UseAbsoluteCoordinates = useAbsoluteCoordinates;
            UseLogicalCoordinates = useLogicalCoordinates;
            HasCoordinateMode = true;
            if (IsActive)
            {
                _owner.OnSessionConfigured();
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return _owner.StartSessionAsync(this, ct);
        }

        public void StopCapture() => _owner.StopSession(this);

        public void Dispose()
        {
            IsActive = false;
            _owner.RemoveSession(this);
            _owner.QueueRecompute();
        }

        internal void Deactivate() => IsActive = false;

        internal void RaiseInputReceived(CapturedInputEventArgs args) =>
            InputReceived?.Invoke(this, args);

        internal void RaiseCaptureError(InputCaptureErrorEventArgs args) =>
            CaptureError?.Invoke(this, args);
    }
}
