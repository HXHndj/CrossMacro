
namespace CrossMacro.Application.Automation;

public sealed class ManageSchedule(IScheduledTaskOperations operations, IScheduledTaskStore store) : IManageSchedule
{
    private readonly IScheduledTaskOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    private readonly IScheduledTaskStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<TaskCollectionResult<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) =>
        new(await LoadAndCheckAsync(cancellationToken).ConfigureAwait(false));

    public Task<ScheduledTask> AddAsync(ScheduledTask task, CancellationToken cancellationToken = default) =>
        MutateAsync(task, add: true, cancellationToken);

    public Task<ScheduledTask> UpdateAsync(ScheduledTask task, CancellationToken cancellationToken = default) =>
        MutateAsync(task, add: false, cancellationToken);
    public async Task<ScheduledTask> RemoveAsync(TaskRequest request, CancellationToken cancellationToken = default)
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
    public async Task<ScheduledTask> SetEnabledAsync(TaskRequest request, CancellationToken cancellationToken = default)
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
    public async Task RunAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var task = await FindAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _operations.RunTaskAsync(task.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ScheduledTask>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _store.LoadAsync().ConfigureAwait(false);
        return _store.Tasks;
    }

    private async Task<ScheduledTask> MutateAsync(
        ScheduledTask task,
        bool add,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (add && tasks.Any(candidate => candidate.Id == task.Id))
        {
            throw new InvalidOperationException($"A schedule task with id '{task.Id}' already exists.");
        }

        var previous = add ? null : tasks.FirstOrDefault(candidate => candidate.Id == task.Id);
        var previousSnapshot = previous is null ? null : Clone(previous);
        cancellationToken.ThrowIfCancellationRequested();
        var mutationApplied = false;
        try
        {
            // Mark the operation before entering the adapter. A synchronous
            // adapter can change its collection and then throw; rollback must
            // still be attempted for that partial mutation.
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
                        // SchedulerService recalculates an enabled task's next
                        // run time while applying an update. Restore the exact
                        // captured value instead of accepting a new wall-clock
                        // calculation during rollback.
                        previous!.NextRunTime = previousSnapshot.NextRunTime;
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

    private static ScheduledTask Clone(ScheduledTask source)
    {
        var clone = new ScheduledTask
        {
            Id = source.Id,
            Name = source.Name,
            MacroFilePath = source.MacroFilePath,
            Type = source.Type,
            PlaybackSpeed = source.PlaybackSpeed,
            IntervalValue = source.IntervalValue,
            IntervalUnit = source.IntervalUnit,
            UseRandomIntervalDelay = source.UseRandomIntervalDelay,
            IntervalMinValue = source.IntervalMinValue,
            IntervalMaxValue = source.IntervalMaxValue,
            ScheduledDateTime = source.ScheduledDateTime,
            WeeklyDays = source.WeeklyDays,
            WeeklyTime = source.WeeklyTime,
            LastRunTime = source.LastRunTime,
            LastStatus = source.LastStatus,
            IsEnabled = source.IsEnabled,
            NextRunTime = source.NextRunTime,
        };

        // NextRunTime is assigned after IsEnabled in the initializer because
        // enabling a task can recalculate this time-dependent value.
        return clone;
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

    private async Task<IReadOnlyList<ScheduledTask>> LoadAndCheckAsync(CancellationToken cancellationToken)
    {
        var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return tasks;
    }

    private async Task<ScheduledTask> FindAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        if (request.Id is not Guid id)
        {
            throw new ArgumentException("A task id is required.", nameof(request));
        }

        var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return tasks.FirstOrDefault(task => task.Id == id)
            ?? throw new KeyNotFoundException($"No schedule task found with id: {id}");
    }
}
