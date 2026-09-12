
namespace CrossMacro.Application.Automation;

public sealed class ManageTrigger(ITriggerTaskOperations operations, ITriggerTaskStore store) : IManageTrigger
{
    private readonly ITriggerTaskOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    private readonly ITriggerTaskStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<TaskCollectionResult<TriggerTask>> ListAsync(CancellationToken cancellationToken = default) =>
        new(await LoadAndCheckAsync(cancellationToken).ConfigureAwait(false));

    public Task<TriggerTask> AddAsync(TriggerTask task, CancellationToken cancellationToken = default) =>
        MutateAsync(task, add: true, cancellationToken);

    public Task<TriggerTask> UpdateAsync(TriggerTask task, CancellationToken cancellationToken = default) =>
        MutateAsync(task, add: false, cancellationToken);
    public async Task<TriggerTask> RemoveAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var task = await FindAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _operations.RemoveTask(task.Id);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _store.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _store.LoadAsync().ConfigureAwait(false);
            throw;
        }

        return task;
    }
    public async Task<TriggerTask> SetEnabledAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var task = await FindAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var previousEnabled = task.IsEnabled;
        try
        {
            _operations.SetTaskEnabled(task.Id, request.Enabled ?? false);
            cancellationToken.ThrowIfCancellationRequested();
            await _store.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _operations.SetTaskEnabled(task.Id, previousEnabled);
            throw;
        }

        return task;
    }

    private async Task<IReadOnlyList<TriggerTask>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _store.LoadAsync().ConfigureAwait(false);
        return _store.Tasks;
    }

    private async Task<TriggerTask> MutateAsync(
        TriggerTask task,
        bool add,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (add && tasks.Any(candidate => candidate.Id == task.Id))
        {
            throw new InvalidOperationException($"A trigger task with id '{task.Id}' already exists.");
        }

        var previous = add ? null : tasks.FirstOrDefault(candidate => candidate.Id == task.Id);
        var previousSnapshot = previous is null ? null : Clone(previous);
        cancellationToken.ThrowIfCancellationRequested();
        var mutationApplied = false;
        try
        {
            // Mark the operation before entering the adapter so a partial
            // synchronous mutation followed by an exception is recoverable.
            mutationApplied = true;
            if (add)
            {
                _operations.AddTask(task);
            }
            else
            {
                _operations.UpdateTask(task);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _store.SaveAsync().ConfigureAwait(false);
            return task;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (mutationApplied)
            {
                RollbackMutation(ex, () =>
                {
                    if (add)
                    {
                        _operations.RemoveTask(task.Id);
                    }
                    else if (previousSnapshot is not null)
                    {
                        _operations.UpdateTask(previousSnapshot);
                    }
                    else
                    {
                        // UpdateTask is normally a no-op for a missing id. Remove
                        // defensively in case a non-standard adapter creates it.
                        _operations.RemoveTask(task.Id);
                    }
                });
            }

            throw;
        }
    }

    private static TriggerTask Clone(TriggerTask source)
    {
        return new TriggerTask
        {
            Id = source.Id,
            Name = source.Name,
            Field = source.Field,
            MatchMode = source.MatchMode,
            Value = source.Value,
            Action = source.Action,
            TargetProfileId = source.TargetProfileId,
            MacroFilePath = source.MacroFilePath,
            FireMode = source.FireMode,
            CooldownMs = source.CooldownMs,
            DebounceMs = source.DebounceMs,
            IsEnabled = source.IsEnabled,
            LastTriggeredTime = source.LastTriggeredTime,
            LastStatus = source.LastStatus,
        };
    }

    private static void RollbackMutation(Exception original, Action rollback)
    {
        try
        {
            rollback();
        }
        catch (Exception rollbackException) when (rollbackException is not OutOfMemoryException)
        {
            throw new AggregateException(
                "Task mutation failed and restoring the previous runtime state also failed.",
                original,
                rollbackException);
        }
    }

    private async Task<IReadOnlyList<TriggerTask>> LoadAndCheckAsync(CancellationToken cancellationToken)
    {
        var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return tasks;
    }

    private async Task<TriggerTask> FindAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        if (request.Id is not Guid id)
        {
            throw new ArgumentException("A task id is required.", nameof(request));
        }

        var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return tasks.FirstOrDefault(task => task.Id == id)
            ?? throw new KeyNotFoundException($"No trigger task found with id: {id}");
    }
}
