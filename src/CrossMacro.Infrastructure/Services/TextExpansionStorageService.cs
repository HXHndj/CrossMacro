
namespace CrossMacro.Infrastructure.Services;

/// <summary>
/// Service for managing text expansion storage in a separate JSON file
/// Follows XDG Base Directory specification
/// </summary>
public class TextExpansionStorageService : ITextExpansionStorageService, IProfileTextExpansionOperationScope

{
    private const string ExpansionsFileName = ConfigFileNames.TextExpansions;
    private List<Core.Models.TextExpansionEntry> _expansions = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _profileOperationGate = new(1, 1);
    private int _loadedState;
    private long _generation;

    public TextExpansionStorageService(string? configDirectory = null)
    {
        configDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? PathHelper.GetConfigDirectory()
            : configDirectory;
        FilePath = Path.Combine(configDirectory, ExpansionsFileName);


        Log.Information("[TextExpansionStorageService] Storage path: {Path}", FilePath);
    }


    /// <summary>
    /// Loads all text expansions from the JSON file synchronously
    /// </summary>
    public IList<Core.Models.TextExpansionEntry> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Log.Information("[TextExpansionStorageService] No existing file found, starting with empty list");
                    _expansions = [];
                    Volatile.Write(ref _loadedState, 1);
                    return CloneEntries(_expansions);
                }

                _expansions = CloneEntries(
                    FileBackedJsonStorage.Read(FilePath, CrossMacroJsonContext.Default.ListTextExpansionEntry) ?? []);
                Volatile.Write(ref _loadedState, 1);

                Log.Information("[TextExpansionStorageService] Loaded {Count} text expansions", _expansions.Count);
                return CloneEntries(_expansions);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogError(ex, "[TextExpansionStorageService] Failed to load text expansions");
                _expansions = [];
                Volatile.Write(ref _loadedState, 1);
                return CloneEntries(_expansions);
            }
        }
    }

    /// <summary>
    /// Loads all text expansions from the JSON file asynchronously
    /// </summary>
    public async Task<IList<Core.Models.TextExpansionEntry>> LoadAsync()
    {
        string filePath;
        long generation;
        lock (_lock)
        {
            filePath = FilePath;
            generation = _generation;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                Log.Information("[TextExpansionStorageService] No existing file found, starting with empty list");
                lock (_lock)
                {
                    if (generation == _generation && string.Equals(FilePath, filePath, StringComparison.Ordinal))
                    {
                        _expansions = [];
                        Volatile.Write(ref _loadedState, 1);
                    }
                }

                return new List<Core.Models.TextExpansionEntry>();
            }

            var loaded = await FileBackedJsonStorage.ReadAsync(filePath, CrossMacroJsonContext.Default.ListTextExpansionEntry)
                .ConfigureAwait(false)
                ?? [];

            lock (_lock)
            {
                if (generation == _generation && string.Equals(FilePath, filePath, StringComparison.Ordinal))
                {
                    _expansions = CloneEntries(loaded);
                    Volatile.Write(ref _loadedState, 1);
                }
            }

            Log.Information("[TextExpansionStorageService] Loaded {Count} text expansions", loaded.Count);
            return CloneEntries(loaded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LogError(ex, "[TextExpansionStorageService] Failed to load text expansions");
            lock (_lock)
            {
                if (generation == _generation && string.Equals(FilePath, filePath, StringComparison.Ordinal))
                {
                    _expansions = [];
                    Volatile.Write(ref _loadedState, 1);
                }
            }

            return [];
        }
    }

    public async Task ReloadAsync(string profileConfigDirectory)
    {
        lock (_lock)
        {
            FilePath = Path.Combine(profileConfigDirectory, ConfigFileNames.TextExpansions);
            _generation++;
            _expansions = [];
            Volatile.Write(ref _loadedState, 0);
        }

        _ = await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Saves all text expansions to the JSON file
    /// </summary>
    public async Task SaveAsync(IEnumerable<Core.Models.TextExpansionEntry> expansions)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(expansions);
            var expansionList = CloneEntries(expansions);
            string filePath;
            long generation;
            lock (_lock)
            {
                filePath = FilePath;
                generation = _generation;
            }

            await FileBackedJsonStorage.WriteAsync(filePath, expansionList, CrossMacroJsonContext.Default.ListTextExpansionEntry)
                .ConfigureAwait(false);

            lock (_lock)
            {
                if (generation == _generation && string.Equals(FilePath, filePath, StringComparison.Ordinal))
                {
                    _expansions = CloneEntries(expansionList);
                    Volatile.Write(ref _loadedState, 1);
                }
            }

            Log.Information("[TextExpansionStorageService] Saved {Count} text expansions", expansionList.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LogError(ex, "[TextExpansionStorageService] Failed to save text expansions");
            throw;
        }
    }

    public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _profileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ProfileOperationLease(_profileOperationGate);
    }


    /// <summary>
    /// Gets the current list of expansions (cached in memory)
    /// </summary>
    public IList<Core.Models.TextExpansionEntry> GetCurrent()
    {
        lock (_lock)
        {
            return CloneEntries(_expansions);
        }
    }

    public bool IsLoaded => Volatile.Read(ref _loadedState) is 1;

    /// <summary>
    /// Gets the file path where expansions are stored
    /// </summary>
    public string FilePath { get; private set; }

    private static List<Core.Models.TextExpansionEntry> CloneEntries(IEnumerable<Core.Models.TextExpansionEntry> entries)
    {
        return entries.Select(entry => new Core.Models.TextExpansionEntry
        {
            Trigger = entry.Trigger,
            Replacement = entry.Replacement,
            IsEnabled = entry.IsEnabled,
            Method = entry.Method,
            InsertionMode = entry.InsertionMode,
            DirectTypingMethod = entry.DirectTypingMethod,
        }).ToList();
    }

    private sealed class ProfileOperationLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = gate;
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) is 0)
            {
                _ = _gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
