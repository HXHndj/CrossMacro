using CrossMacro.Application.Automation;

namespace CrossMacro.Infrastructure.Tests.Services;

public sealed class ManageTextExpansionTests
{
    [Fact]
    public async Task ListAsync_UsesLoadedStorageSnapshot()
    {
        var storage = Substitute.For<ITextExpansionStorageService>();
        var profileManager = Substitute.For<IProfileManager>();
        _ = profileManager.ActiveProfile.Returns(new ProfileInfo());
        var expected = new List<TextExpansionEntry>
        {
            new(":mail", "me@example.com"),
        };
        storage.IsLoaded.Returns(true);
        storage.GetCurrent().Returns(expected);

        var service = new ManageTextExpansion(storage, profileManager);

        var result = await service.ListAsync();

        _ = result.Should().BeEquivalentTo(expected);
        _ = storage.DidNotReceive().LoadAsync();
    }

    [Fact]
    public async Task ListAsync_ForNonActiveProfile_UsesProfileScopedStoreWithoutReloadingActiveStorage()
    {
        var storage = Substitute.For<ITextExpansionStorageService>();
        var profileStore = Substitute.For<IProfileTextExpansionStore>();
        var profileManager = Substitute.For<IProfileManager>();
        var active = new ProfileInfo { Id = "default", Name = "Default" };
        var target = new ProfileInfo { Id = "work", Name = "Work" };
        _ = profileManager.ActiveProfile.Returns(active);
        _ = profileManager.Profiles.Returns(new[] { active, target });
        _ = profileManager.GetProfileDirectory("work").Returns("/tmp/crossmacro-work");
        _ = profileStore.LoadAsync("/tmp/crossmacro-work", Arg.Any<CancellationToken>())
            .Returns(new List<TextExpansionEntry> { new(":work", "value") });

        var service = new ManageTextExpansion(storage, profileManager, profileStore);

        var result = await service.ListAsync("work");

        _ = result.Should().ContainSingle().Which.Trigger.Should().Be(":work");
        await profileStore.Received(1).LoadAsync("/tmp/crossmacro-work", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().ReloadAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ListAsync_WhenLegacyProfileReloadFails_RestoresActiveProfileState()
    {
        var storage = Substitute.For<ITextExpansionStorageService>();
        var profileManager = Substitute.For<IProfileManager>();
        var active = new ProfileInfo { Id = "default", Name = "Default" };
        var target = new ProfileInfo { Id = "work", Name = "Work" };
        _ = profileManager.ActiveProfile.Returns(active);
        _ = profileManager.Profiles.Returns(new[] { active, target });
        _ = profileManager.GetProfileDirectory("work").Returns("/tmp/crossmacro-work");
        _ = profileManager.GetProfileDirectory("default").Returns("/tmp/crossmacro-default");
        _ = storage.ReloadAsync("/tmp/crossmacro-work")
            .Returns(Task.FromException(new IOException("profile reload failed")));

        var act = async () => await new ManageTextExpansion(storage, profileManager).ListAsync("work");

        _ = await act.Should().ThrowAsync<IOException>();
        await storage.Received(1).ReloadAsync("/tmp/crossmacro-default");
    }

    [Fact]
    public async Task ActiveMutation_HoldsProfileScopeUntilSaveCompletesBeforeSwitchReload()
    {
        var storage = new ScopedTextExpansionStorage();
        var profileManager = Substitute.For<IProfileManager>();
        _ = profileManager.ActiveProfile.Returns(new ProfileInfo { Id = "default", Name = "Default" });
        var service = new ManageTextExpansion(storage, profileManager);

        var mutation = service.AddAsync(new TextExpansionEntry(":active", "value"));
        await storage.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var switchTask = Task.Run(async () =>
        {
            await using var scope = await storage.EnterAsync();
            await storage.ReloadAsync("/tmp/profile-b");
        });

        _ = switchTask.IsCompleted.Should().BeFalse();
        _ = storage.AllowSave.TrySetResult();

        await mutation;
        await switchTask;
        _ = storage.Events.Should().Equal("save-start", "save-end", "reload:/tmp/profile-b");
    }

    [Fact]
    public async Task SwitchScopeFirst_MakesActiveMutationWaitForReloadBeforeLoading()
    {
        var storage = new ScopedTextExpansionStorage();
        var profileManager = Substitute.For<IProfileManager>();
        _ = profileManager.ActiveProfile.Returns(new ProfileInfo { Id = "default", Name = "Default" });
        var service = new ManageTextExpansion(storage, profileManager);
        var switchScope = await storage.EnterAsync();

        await storage.ReloadAsync("/tmp/profile-b");
        var mutation = service.AddAsync(new TextExpansionEntry(":active", "value"));

        _ = mutation.IsCompleted.Should().BeFalse();
        await switchScope.DisposeAsync();
        await storage.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = storage.AllowSave.TrySetResult();

        await mutation;
        _ = storage.Events.Should().Equal("reload:/tmp/profile-b", "save-start", "save-end");
    }

    private sealed class ScopedTextExpansionStorage : ITextExpansionStorageService, IProfileTextExpansionOperationScope
    {
        private readonly SemaphoreSlim _profileGate = new(1, 1);
        private readonly List<TextExpansionEntry> _entries = [];

        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
        public bool IsLoaded => true;
        public string FilePath => "/tmp/profile-a/text-expansions.json";

        public IList<TextExpansionEntry> Load() => _entries.ToList();

        public Task<IList<TextExpansionEntry>> LoadAsync() => Task.FromResult<IList<TextExpansionEntry>>(_entries.ToList());

        public async Task ReloadAsync(string profileConfigDirectory)
        {
            Events.Add($"reload:{profileConfigDirectory}");
            await Task.CompletedTask;
        }

        public async Task SaveAsync(IEnumerable<TextExpansionEntry> expansions)
        {
            Events.Add("save-start");
            _ = SaveStarted.TrySetResult();
            await AllowSave.Task;
            _entries.Clear();
            _entries.AddRange(expansions);
            Events.Add("save-end");
        }

        public IList<TextExpansionEntry> GetCurrent() => _entries.ToList();

        public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
        {
            await _profileGate.WaitAsync(cancellationToken);
            return new GateLease(_profileGate);
        }

        private sealed class GateLease(SemaphoreSlim gate) : IAsyncDisposable
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
}
