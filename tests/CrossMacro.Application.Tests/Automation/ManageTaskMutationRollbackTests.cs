namespace CrossMacro.Application.Tests.Automation;

public sealed class ManageTaskMutationRollbackTests
{
    [Fact]
    public async Task ManageSchedule_Add_WhenCancellationFollowsMutation_RestoresRuntimeCollection()
    {
        var operations = Substitute.For<IScheduledTaskOperations>();
        var store = Substitute.For<IScheduledTaskStore>();
        var tasks = new ObservableCollection<ScheduledTask>();
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var added = new ScheduledTask { Id = Guid.NewGuid(), Name = "added" };

        operations.When(x => x.AddTask(Arg.Any<ScheduledTask>())).Do(callInfo =>
        {
            tasks.Add(callInfo.Arg<ScheduledTask>()!);
            cancellation.Cancel();
        });
        operations.When(x => x.RemoveTask(Arg.Any<Guid>())).Do(callInfo =>
        {
            var task = tasks.FirstOrDefault(candidate => candidate.Id == callInfo.Arg<Guid>());
            if (task is not null)
            {
                _ = tasks.Remove(task);
            }
        });

        var workflow = new ManageSchedule(operations, store);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.AddAsync(added, cancellation.Token));

        Assert.Empty(tasks);
        _ = store.DidNotReceive().SaveAsync();
        operations.Received(1).RemoveTask(added.Id);
    }

    [Fact]
    public async Task ManageSchedule_Update_WhenSaveFails_RestoresRuntimeFieldsAndScheduleTime()
    {
        var operations = Substitute.For<IScheduledTaskOperations>();
        var store = Substitute.For<IScheduledTaskStore>();
        var nextRun = DateTime.UtcNow.AddHours(2);
        var existing = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = "before",
            MacroFilePath = "before.macro",
            PlaybackSpeed = 0.75,
            IntervalValue = 30,
            IntervalUnit = IntervalUnit.Seconds,
            IsEnabled = true,
            LastStatus = "before status",
            NextRunTime = nextRun,
        };
        var tasks = new ObservableCollection<ScheduledTask> { existing };
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        _ = store.SaveAsync().Returns(Task.FromException(new IOException("disk full")));
        var updated = new ScheduledTask
        {
            Id = existing.Id,
            Name = "after",
            MacroFilePath = "after.macro",
            PlaybackSpeed = 1.5,
            IntervalValue = 5,
            IntervalUnit = IntervalUnit.Minutes,
            IsEnabled = false,
            LastStatus = "after status",
        };

        operations.When(x => x.UpdateTask(Arg.Any<ScheduledTask>())).Do(callInfo =>
        {
            ApplyScheduledTaskUpdate(existing, callInfo.Arg<ScheduledTask>()!);
        });

        var workflow = new ManageSchedule(operations, store);

        _ = await Assert.ThrowsAsync<IOException>(() => workflow.UpdateAsync(updated, CancellationToken.None));

        Assert.Same(existing, tasks.Single());
        Assert.Equal("before", existing.Name);
        Assert.Equal("before.macro", existing.MacroFilePath);
        Assert.Equal(0.75, existing.PlaybackSpeed);
        Assert.True(existing.IsEnabled);
        Assert.Equal("before status", existing.LastStatus);
        Assert.Equal(nextRun, existing.NextRunTime);
        operations.Received(2).UpdateTask(Arg.Any<ScheduledTask>());
    }

    [Fact]
    public async Task ManageShortcut_Add_WhenCancellationFollowsMutation_RestoresRuntimeCollection()
    {
        var operations = Substitute.For<IShortcutTaskOperations>();
        var store = Substitute.For<IShortcutTaskStore>();
        var tasks = new ObservableCollection<ShortcutTask>();
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var added = new ShortcutTask { Id = Guid.NewGuid(), Name = "added" };

        operations.When(x => x.AddTask(Arg.Any<ShortcutTask>())).Do(callInfo =>
        {
            tasks.Add(callInfo.Arg<ShortcutTask>()!);
            cancellation.Cancel();
        });
        operations.When(x => x.RemoveTask(Arg.Any<Guid>())).Do(callInfo =>
        {
            var task = tasks.FirstOrDefault(candidate => candidate.Id == callInfo.Arg<Guid>());
            if (task is not null)
            {
                _ = tasks.Remove(task);
            }
        });

        var workflow = new ManageShortcut(operations, store);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.AddAsync(added, cancellation.Token));

        Assert.Empty(tasks);
        _ = store.DidNotReceive().SaveAsync();
        operations.Received(1).RemoveTask(added.Id);
    }

    [Fact]
    public async Task ManageShortcut_Update_WhenSaveFails_RestoresRuntimeFieldsAndWindowRules()
    {
        var operations = Substitute.For<IShortcutTaskOperations>();
        var store = Substitute.For<IShortcutTaskStore>();
        var existing = new ShortcutTask
        {
            Id = Guid.NewGuid(),
            Name = "before",
            MacroFilePath = "before.macro",
            HotkeyString = "Ctrl+A",
            PlaybackSpeed = 0.8,
            IsEnabled = false,
            LoopEnabled = true,
            RepeatCount = 2,
            RepeatDelayMs = 20,
            LastStatus = "before status",
            LastTriggeredTime = DateTime.UtcNow.AddMinutes(-2),
        };
        existing.WindowRules.Add(new ShortcutWindowRule
        {
            Field = TriggerField.WindowClass,
            MatchMode = TriggerMatchMode.Equals,
            Value = "before-class",
        });
        var tasks = new ObservableCollection<ShortcutTask> { existing };
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        _ = store.SaveAsync().Returns(Task.FromException(new IOException("disk full")));
        var updated = new ShortcutTask
        {
            Id = existing.Id,
            Name = "after",
            MacroFilePath = "after.macro",
            HotkeyString = "Ctrl+B",
            PlaybackSpeed = 1.25,
            IsEnabled = true,
            LoopEnabled = false,
            RunWhileHeld = true,
            RepeatCount = 9,
            RepeatDelayMs = 100,
            LastStatus = "after status",
            LastTriggeredTime = DateTime.UtcNow,
        };
        updated.WindowRules.Add(new ShortcutWindowRule
        {
            Field = TriggerField.ProcessName,
            MatchMode = TriggerMatchMode.Regex,
            Value = "after-process",
        });

        operations.When(x => x.UpdateTask(Arg.Any<ShortcutTask>())).Do(callInfo =>
        {
            ApplyShortcutTaskUpdate(existing, callInfo.Arg<ShortcutTask>()!);
        });

        var workflow = new ManageShortcut(operations, store);

        _ = await Assert.ThrowsAsync<IOException>(() => workflow.UpdateAsync(updated, CancellationToken.None));

        Assert.Same(existing, tasks.Single());
        Assert.Equal("before", existing.Name);
        Assert.Equal("before.macro", existing.MacroFilePath);
        Assert.Equal("Ctrl+A", existing.HotkeyString);
        Assert.Equal(0.8, existing.PlaybackSpeed);
        Assert.True(existing.LoopEnabled);
        Assert.False(existing.RunWhileHeld);
        Assert.Equal(2, existing.RepeatCount);
        Assert.Equal("before status", existing.LastStatus);
        var restoredRule = Assert.Single(existing.WindowRules);
        Assert.Equal(TriggerField.WindowClass, restoredRule.Field);
        Assert.Equal(TriggerMatchMode.Equals, restoredRule.MatchMode);
        Assert.Equal("before-class", restoredRule.Value);
        operations.Received(2).UpdateTask(Arg.Any<ShortcutTask>());
    }

    [Fact]
    public async Task ManageTrigger_Add_WhenCancellationFollowsMutation_RestoresRuntimeCollection()
    {
        var operations = Substitute.For<ITriggerTaskOperations>();
        var store = Substitute.For<ITriggerTaskStore>();
        var tasks = new ObservableCollection<TriggerTask>();
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var added = new TriggerTask { Id = Guid.NewGuid(), Name = "added" };

        operations.When(x => x.AddTask(Arg.Any<TriggerTask>())).Do(callInfo =>
        {
            tasks.Add(callInfo.Arg<TriggerTask>()!);
            cancellation.Cancel();
        });
        operations.When(x => x.RemoveTask(Arg.Any<Guid>())).Do(callInfo =>
        {
            var task = tasks.FirstOrDefault(candidate => candidate.Id == callInfo.Arg<Guid>());
            if (task is not null)
            {
                _ = tasks.Remove(task);
            }
        });

        var workflow = new ManageTrigger(operations, store);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.AddAsync(added, cancellation.Token));

        Assert.Empty(tasks);
        _ = store.DidNotReceive().SaveAsync();
        operations.Received(1).RemoveTask(added.Id);
    }

    [Fact]
    public async Task ManageTrigger_Update_WhenSaveFails_RestoresRuntimeFields()
    {
        var operations = Substitute.For<ITriggerTaskOperations>();
        var store = Substitute.For<ITriggerTaskStore>();
        var lastTriggered = DateTime.UtcNow.AddMinutes(-3);
        var existing = new TriggerTask
        {
            Id = Guid.NewGuid(),
            Name = "before",
            Field = TriggerField.WindowTitle,
            MatchMode = TriggerMatchMode.Contains,
            Value = "before title",
            Action = TriggerOperation.SwitchProfile,
            TargetProfileId = "before-profile",
            FireMode = TriggerFireMode.OnceOnChange,
            CooldownMs = 100,
            DebounceMs = 200,
            IsEnabled = true,
            LastTriggeredTime = lastTriggered,
            LastStatus = "before status",
        };
        var tasks = new ObservableCollection<TriggerTask> { existing };
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        _ = store.SaveAsync().Returns(Task.FromException(new IOException("disk full")));
        var updated = new TriggerTask
        {
            Id = existing.Id,
            Name = "after",
            Field = TriggerField.ProcessName,
            MatchMode = TriggerMatchMode.Equals,
            Value = "after process",
            Action = TriggerOperation.RunMacro,
            MacroFilePath = "after.macro",
            FireMode = TriggerFireMode.EveryMatch,
            CooldownMs = 900,
            DebounceMs = 800,
            IsEnabled = false,
            LastTriggeredTime = DateTime.UtcNow,
            LastStatus = "after status",
        };

        operations.When(x => x.UpdateTask(Arg.Any<TriggerTask>())).Do(callInfo =>
        {
            ApplyTriggerTaskUpdate(existing, callInfo.Arg<TriggerTask>()!);
        });

        var workflow = new ManageTrigger(operations, store);

        _ = await Assert.ThrowsAsync<IOException>(() => workflow.UpdateAsync(updated, CancellationToken.None));

        Assert.Same(existing, tasks.Single());
        Assert.Equal("before", existing.Name);
        Assert.Equal(TriggerField.WindowTitle, existing.Field);
        Assert.Equal(TriggerMatchMode.Contains, existing.MatchMode);
        Assert.Equal("before title", existing.Value);
        Assert.Equal(TriggerOperation.SwitchProfile, existing.Action);
        Assert.Equal("before-profile", existing.TargetProfileId);
        Assert.Equal(TriggerFireMode.OnceOnChange, existing.FireMode);
        Assert.Equal(100, existing.CooldownMs);
        Assert.Equal(200, existing.DebounceMs);
        Assert.True(existing.IsEnabled);
        Assert.Equal(lastTriggered, existing.LastTriggeredTime);
        Assert.Equal("before status", existing.LastStatus);
        operations.Received(2).UpdateTask(Arg.Any<TriggerTask>());
    }

    [Fact]
    public async Task ManageSchedule_Add_WhenSaveAndRollbackFail_PreservesBothExceptions()
    {
        var operations = Substitute.For<IScheduledTaskOperations>();
        var store = Substitute.For<IScheduledTaskStore>();
        var tasks = new ObservableCollection<ScheduledTask>();
        _ = store.Tasks.Returns(tasks);
        _ = store.LoadAsync().Returns(Task.CompletedTask);
        _ = store.SaveAsync().Returns(Task.FromException(new IOException("save failed")));
        var added = new ScheduledTask { Id = Guid.NewGuid() };
        var rollbackException = new InvalidOperationException("remove failed");

        operations.When(x => x.AddTask(Arg.Any<ScheduledTask>())).Do(callInfo => tasks.Add(callInfo.Arg<ScheduledTask>()!));
        operations.When(x => x.RemoveTask(Arg.Any<Guid>())).Do(_ => throw rollbackException);

        var workflow = new ManageSchedule(operations, store);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => workflow.AddAsync(added, CancellationToken.None));

        Assert.Contains(aggregate.InnerExceptions, exception => exception is IOException io && string.Equals(io.Message, "save failed", StringComparison.Ordinal));
        Assert.Contains(aggregate.InnerExceptions, exception => ReferenceEquals(exception, rollbackException));
        Assert.Equal(1, tasks.Count);
    }

    private static void ApplyScheduledTaskUpdate(ScheduledTask existing, ScheduledTask updated)
    {
        existing.Name = updated.Name;
        existing.MacroFilePath = updated.MacroFilePath;
        existing.Type = updated.Type;
        existing.PlaybackSpeed = updated.PlaybackSpeed;
        existing.IntervalValue = updated.IntervalValue;
        existing.IntervalUnit = updated.IntervalUnit;
        existing.UseRandomIntervalDelay = updated.UseRandomIntervalDelay;
        existing.IntervalMinValue = updated.IntervalMinValue;
        existing.IntervalMaxValue = updated.IntervalMaxValue;
        existing.ScheduledDateTime = updated.ScheduledDateTime;
        existing.WeeklyDays = updated.WeeklyDays;
        existing.WeeklyTime = updated.WeeklyTime;
        existing.LastRunTime = updated.LastRunTime;
        existing.LastStatus = updated.LastStatus;
        existing.NextRunTime = updated.IsEnabled ? DateTime.UtcNow.AddMinutes(1) : null;
        existing.IsEnabled = updated.IsEnabled;
    }

    private static void ApplyShortcutTaskUpdate(ShortcutTask existing, ShortcutTask updated)
    {
        existing.Name = updated.Name;
        existing.MacroFilePath = updated.MacroFilePath;
        existing.HotkeyString = updated.HotkeyString;
        existing.PlaybackSpeed = updated.PlaybackSpeed;
        existing.IsEnabled = updated.IsEnabled;
        existing.LoopEnabled = updated.LoopEnabled;
        existing.RepeatCount = updated.RepeatCount;
        existing.RepeatDelayMs = updated.RepeatDelayMs;
        existing.UseRandomRepeatDelay = updated.UseRandomRepeatDelay;
        existing.RepeatDelayMinMs = updated.RepeatDelayMinMs;
        existing.RepeatDelayMaxMs = updated.RepeatDelayMaxMs;
        existing.RunWhileHeld = updated.RunWhileHeld;
        existing.WindowRules.Clear();
        foreach (var rule in updated.WindowRules)
        {
            existing.WindowRules.Add(new ShortcutWindowRule
            {
                Field = rule.Field,
                MatchMode = rule.MatchMode,
                Value = rule.Value,
            });
        }

        existing.LastStatus = updated.LastStatus;
        existing.LastTriggeredTime = updated.LastTriggeredTime;
    }

    private static void ApplyTriggerTaskUpdate(TriggerTask existing, TriggerTask updated)
    {
        existing.Name = updated.Name;
        existing.Field = updated.Field;
        existing.MatchMode = updated.MatchMode;
        existing.Value = updated.Value;
        existing.Action = updated.Action;
        existing.TargetProfileId = updated.TargetProfileId;
        existing.MacroFilePath = updated.MacroFilePath;
        existing.FireMode = updated.FireMode;
        existing.CooldownMs = updated.CooldownMs;
        existing.DebounceMs = updated.DebounceMs;
        existing.IsEnabled = updated.IsEnabled;
        existing.LastTriggeredTime = updated.LastTriggeredTime;
        existing.LastStatus = updated.LastStatus;
    }
}
