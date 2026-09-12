namespace CrossMacro.Infrastructure.Services;

public sealed class ProfileRuntimeCoordinator : IProfileManager, IProfileSwitchRequestHandler, IDisposable
{
    private readonly IProfileCatalog _catalog;
    private readonly ISettingsService _settingsService;
    private readonly IHotkeyConfigurationService _hotkeyConfigService;
    private readonly HotkeySettings _hotkeySettings;
    private readonly IGlobalHotkeyService? _hotkeyService;
    private readonly IShortcutService? _shortcutService;
    private readonly ISchedulerService? _schedulerService;
    private readonly ITextExpansionService? _textExpansionService;
    private readonly ITriggerService _triggerService;
    private readonly IScheduledTaskRepository _scheduledTaskRepository;
    private readonly ITextExpansionStorageService _textExpansionStorageService;
    private readonly ProfileRuntimeState? _runtimeState;
    private readonly IReadOnlyList<IProfileRuntimeParticipant> _profileRuntimeParticipants;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _initialized;
    private int _disposed;

    internal ProfileRuntimeCoordinator(
        IProfileCatalog catalog,
        ISettingsService settingsService,
        IHotkeyConfigurationService hotkeyConfigService,
        HotkeySettings hotkeySettings,
        IGlobalHotkeyService? hotkeyService,
        IShortcutService? shortcutService,
        ISchedulerService? schedulerService,
        ITextExpansionService? textExpansionService,
        ITriggerService triggerService,
        IScheduledTaskRepository scheduledTaskRepository,
        ITextExpansionStorageService textExpansionStorageService,
        ProfileRuntimeState? runtimeState = null,
        IEnumerable<IProfileRuntimeParticipant>? profileRuntimeParticipants = null)
    {
        _catalog = catalog;
        _settingsService = settingsService;
        _hotkeyConfigService = hotkeyConfigService;
        _hotkeySettings = hotkeySettings;
        _hotkeyService = hotkeyService;
        _shortcutService = shortcutService;
        _schedulerService = schedulerService;
        _textExpansionService = textExpansionService;
        _triggerService = triggerService;
        _scheduledTaskRepository = scheduledTaskRepository;
        _textExpansionStorageService = textExpansionStorageService;
        _runtimeState = runtimeState;
        _profileRuntimeParticipants = profileRuntimeParticipants?.ToArray() ?? [];
    }

    public ProfileInfo ActiveProfile => _catalog.ActiveProfile;
    public IReadOnlyList<ProfileInfo> Profiles => _catalog.Profiles;
    public bool IsInitialized => Volatile.Read(ref _initialized) is 1;
    public event EventHandler<ProfileChangedEventArgs>? ProfileChanged;

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) is 1)
            {
                return;
            }

            await _catalog.InitializeAsync().ConfigureAwait(false);
            await ReloadProfileServicesAsync(_catalog.GetProfileDirectory(_catalog.ActiveProfile.Id)).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
            _runtimeState?.MarkInitialized();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public Task HandleSwitchRequestAsync(string profileId) => SwitchProfileAsync(profileId);

    public async Task SwitchProfileAsync(string profileId)
    {
        ProfileInfo activeProfile;
        IAsyncDisposable? profileScope = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previousProfile = _catalog.ActiveProfile;
            var profile = _catalog.Profiles.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Profile '{profileId}' does not exist.");

            if (string.Equals(profile.Id, previousProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            profileScope = await EnterActiveProfileScopeAsync().ConfigureAwait(false);
            var profileDir = _catalog.GetProfileDirectory(profile.Id);
            var hotkeyWasRunning = _hotkeyService?.IsRunning ?? false;
            var shortcutWasListening = _shortcutService?.IsListening ?? false;
            var schedulerWasRunning = _schedulerService?.IsRunning ?? false;
            var textExpansionWasRunning = _textExpansionService?.IsRunning ?? false;
            var triggerWasMonitoring = _triggerService.IsMonitoring;

            await FlushProfileRuntimeParticipantsAsync().ConfigureAwait(false);

            var stopResult = await StopRuntimeServicesAsync().ConfigureAwait(false);
            if (!stopResult.Succeeded)
            {
                await RestartRuntimeServicesAsync(
                    hotkeyWasRunning && stopResult.HotkeyStopped,
                    shortcutWasListening && stopResult.ShortcutStopped,
                    schedulerWasRunning && stopResult.SchedulerStopped,
                    textExpansionWasRunning && stopResult.TextExpansionStopped,
                    triggerWasMonitoring && stopResult.TriggerStopped).ConfigureAwait(false);

                var message = stopResult.SchedulerQuiesced
                    ? "Profile switch aborted because one or more runtime services did not stop cleanly."
                    : "Profile switch aborted because the scheduler did not quiesce.";
                var stopError = stopResult.Errors.Count > 0
                    ? new AggregateException("Runtime service stop failed.", stopResult.Errors)
                    : null;
                throw new InvalidOperationException(message, stopError);
            }

            try
            {
                await ReloadProfileServicesAsync(profileDir).ConfigureAwait(false);
                await ReloadProfileRuntimeParticipantsAsync(profileDir).ConfigureAwait(false);
                await _catalog.SetActiveProfileAsync(profile.Id).ConfigureAwait(false);
                activeProfile = _catalog.ActiveProfile;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _catalog.RestoreActiveProfile(previousProfile.Id);
                await ReloadProfileServicesAsync(_catalog.GetProfileDirectory(previousProfile.Id)).ConfigureAwait(false);
                await ReloadProfileRuntimeParticipantsAsync(_catalog.GetProfileDirectory(previousProfile.Id)).ConfigureAwait(false);
                await RestartRuntimeServicesAsync(
                    hotkeyWasRunning,
                    shortcutWasListening,
                    schedulerWasRunning,
                    textExpansionWasRunning,
                    triggerWasMonitoring).ConfigureAwait(false);
                throw;
            }

            await RestartRuntimeServicesAsync(
                hotkeyWasRunning,
                shortcutWasListening,
                schedulerWasRunning,
                textExpansionWasRunning,
                triggerWasMonitoring).ConfigureAwait(false);

            Log.Information("Switched active profile to {ProfileId}", profile.Id);
        }
        finally
        {
            try
            {
                if (profileScope is not null)
                {
                    await profileScope.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        ProfileChanged?.Invoke(this, new ProfileChangedEventArgs(activeProfile));
    }

    public async Task<ProfileInfo> CreateProfileAsync(string displayName)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await _catalog.CreateProfileAsync(displayName).ConfigureAwait(false); }
        finally { _ = _gate.Release(); }
    }

    public async Task RenameProfileAsync(string profileId, string newDisplayName)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _catalog.RenameProfileAsync(profileId, newDisplayName).ConfigureAwait(false); }
        finally { _ = _gate.Release(); }
    }

    public async Task DeleteProfileAsync(string profileId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _catalog.DeleteProfileAsync(profileId).ConfigureAwait(false); }
        finally { _ = _gate.Release(); }
    }

    public string GetProfileDirectory(string profileId) => _catalog.GetProfileDirectory(profileId);

    private async Task<RuntimeStopResult> StopRuntimeServicesAsync()
    {
        var errors = new List<Exception>();
        var textExpansionStopped = _textExpansionService is null || !_textExpansionService.IsRunning;
        try
        {
            if (_textExpansionService is not null)
            {
                await _textExpansionService.StopExpansionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            textExpansionStopped = _textExpansionService is null || !_textExpansionService.IsRunning;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(ex);
            Log.Warning(ex, "Failed to stop text expansion service");
        }

        var schedulerStopped = _schedulerService is null;
        try
        {
            if (_schedulerService is not null)
            {
                await _schedulerService.StopAsync(CancellationToken.None).ConfigureAwait(false);
                schedulerStopped = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(ex);
            Log.Warning(ex, "Failed to stop scheduler service");
        }

        var schedulerQuiesced = _schedulerService is null || (schedulerStopped && _schedulerService.Completion.IsCompleted);
        if (!schedulerQuiesced)
        {
            Log.Warning("Profile switch aborted because the scheduler lifetime is still active after shutdown timeout");
        }

        var triggerStopped = !_triggerService.IsMonitoring;
        try
        {
            _triggerService.StopMonitoring();
            triggerStopped = !_triggerService.IsMonitoring;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(ex);
            Log.Warning(ex, "Failed to stop trigger service");
        }

        var shortcutStopped = _shortcutService is null || !_shortcutService.IsListening;
        try
        {
            _shortcutService?.StopShortcuts();
            shortcutStopped = _shortcutService is null || !_shortcutService.IsListening;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(ex);
            Log.Warning(ex, "Failed to stop shortcut service");
        }

        var hotkeyStopped = _hotkeyService is null || !_hotkeyService.IsRunning;
        try
        {
            if (_hotkeyService is not null)
            {
                await _hotkeyService.StopHotkeyServiceAsync(CancellationToken.None).ConfigureAwait(false);
                hotkeyStopped = !_hotkeyService.IsRunning;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(ex);
            Log.Warning(ex, "Failed to stop hotkey service");
        }

        return new RuntimeStopResult(
            schedulerQuiesced,
            schedulerQuiesced,
            hotkeyStopped,
            shortcutStopped,
            textExpansionStopped,
            triggerStopped,
            errors);
    }

    private async Task<IAsyncDisposable?> EnterActiveProfileScopeAsync()
    {
        return _textExpansionStorageService is IProfileTextExpansionOperationScope scope
            ? await scope.EnterAsync(CancellationToken.None).ConfigureAwait(false)
            : null;
    }

    private async Task ReloadProfileServicesAsync(string profileDir)
    {
        await _settingsService.ReloadAsync(profileDir).ConfigureAwait(false);
        var loaded = await _hotkeyConfigService.ReloadAsync(profileDir).ConfigureAwait(false)
            ?? await _hotkeyConfigService.LoadAsync().ConfigureAwait(false);
        _hotkeySettings.RecordingHotkey = loaded.RecordingHotkey;
        _hotkeySettings.PlaybackHotkey = loaded.PlaybackHotkey;
        _hotkeySettings.PauseHotkey = loaded.PauseHotkey;
        _hotkeyService?.ApplyHotkeys(_hotkeySettings.RecordingHotkey, _hotkeySettings.PlaybackHotkey, _hotkeySettings.PauseHotkey);
        if (_shortcutService is not null)
        {
            await _shortcutService.ReloadAsync(profileDir).ConfigureAwait(false);
        }
        await _triggerService.ReloadAsync(profileDir).ConfigureAwait(false);
        await _scheduledTaskRepository.ReloadAsync(profileDir).ConfigureAwait(false);
        if (_schedulerService is not null)
        {
            await _schedulerService.LoadAsync().ConfigureAwait(false);
        }
        await _textExpansionStorageService.ReloadAsync(profileDir).ConfigureAwait(false);
    }

    private async Task RestartRuntimeServicesAsync(bool hotkeyWasRunning, bool shortcutWasListening, bool schedulerWasRunning, bool textExpansionWasRunning, bool triggerWasMonitoring)
    {
        if (hotkeyWasRunning && _hotkeyService is not null)
        {
            try { _hotkeyService.Start(); _hotkeyService.ApplyHotkeys(_hotkeySettings.RecordingHotkey, _hotkeySettings.PlaybackHotkey, _hotkeySettings.PauseHotkey); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warning(ex, "Failed to restart hotkey service after profile switch"); }
        }
        if (shortcutWasListening && _shortcutService is not null) { try { _shortcutService.Start(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warning(ex, "Failed to restart shortcut service after profile switch"); } }
        if (triggerWasMonitoring) { try { _triggerService.Start(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warning(ex, "Failed to restart trigger service after profile switch"); } }
        if (schedulerWasRunning && _schedulerService is not null) { try { _schedulerService.Start(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warning(ex, "Failed to restart scheduler after profile switch"); } }
        if (textExpansionWasRunning && _textExpansionService is not null && _settingsService.Current.EnableTextExpansion)
        {
            try { await _textExpansionService.StartAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warning(ex, "Failed to restart text expansion after profile switch"); }
        }
    }

    private async Task FlushProfileRuntimeParticipantsAsync()
    {
        foreach (var participant in _profileRuntimeParticipants)
        {
            await participant.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ReloadProfileRuntimeParticipantsAsync(string profileConfigDirectory)
    {
        foreach (var participant in _profileRuntimeParticipants)
        {
            await participant.ReloadAsync(profileConfigDirectory, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 0)
        {
            _gate.Dispose();
        }
    }

    private sealed record RuntimeStopResult(
        bool SchedulerQuiesced,
        bool SchedulerStopped,
        bool HotkeyStopped,
        bool ShortcutStopped,
        bool TextExpansionStopped,
        bool TriggerStopped,
        IReadOnlyList<Exception> Errors)
    {
        public bool Succeeded => SchedulerQuiesced && Errors.Count is 0;
    }
}
