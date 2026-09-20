namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class ViewLibraryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeezTailViews-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope;
    private readonly JsonLogFileRepository _files = new();
    private readonly JsonLogGroupRepository _groups;
    private readonly FaultStore _store = new(new JsonViewLibraryRepository());

    public ViewLibraryServiceTests()
    {
        _scope = AppPaths.BeginTestScope(rootPath: _root);
        _groups = new(_files);
    }

    private ViewLibraryService Service(DashboardMutationCoordinator? coordinator = null)
    {
        var catalog = new LogFileCatalogService(_files);
        return new(_store, _groups, catalog, coordinator ?? new(), async entries =>
            await catalog.RemoveCreatedEntriesIfUnreferencedAsync(entries, async () =>
                (await _groups.GetAllAsync()).SelectMany(g => g.FileIds).ToHashSet()));
    }

    [Fact]
    public async Task LinkedView_RefreshPreservesIds_CopyIsIndependent_AndMutationsAreGuarded()
    {
        var coordinator = new DashboardMutationCoordinator();
        var service = Service(coordinator);
        await service.InitializeAsync();
        coordinator.CanEdit = () => !service.IsReadOnly;
        var source = new ViewSourceRegistration { Name = "Team", Location = "unavailable" };
        var snapshot = new ViewSourceSnapshot { Name = "Team", Revision = "abc", Views =
            [new SavedView { Id = "teamview", Name = "Team View", Definition = new ViewExport { Groups =
                [new ViewExportGroup { Id = "shared", Name = "Original" }] } }] };
        await service.AcceptSourceAsync(source, snapshot);
        var identity = new ViewIdentity(source.Id, "teamview");
        await service.ActivateAsync(identity);
        var runtimeId = Assert.Single(await _groups.GetAllAsync()).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(() => _groups.ReplaceAllAsync([])));
        snapshot.Revision = "def";
        snapshot.Views[0].Definition.Groups[0].Name = "Updated";
        await service.AcceptSourceAsync(source, snapshot);
        Assert.Equal(runtimeId, Assert.Single(await _groups.GetAllAsync()).Id);
        Assert.Equal("Updated", Assert.Single(await _groups.GetAllAsync()).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveSourceAsync(source.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptSourceAsync(source, new ViewSourceSnapshot { Revision = "empty" }));
        await service.CreateAsync("Local copy", true);
        Assert.NotEqual(runtimeId, Assert.Single(await _groups.GetAllAsync()).Id);
        await coordinator.ExecuteAsync(() => _groups.ReplaceAllAsync([]));
        await service.ActivateAsync(identity);
        Assert.Equal("Updated", Assert.Single(await _groups.GetAllAsync()).Name);
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal(identity, restarted.Library!.Active);
        Assert.Equal(3, (await restarted.ListAsync()).Count);
    }

    [Fact]
    public async Task Migration_SwitchingAndRestart_PreserveLatestEditsAndIdentities()
    {
        await _groups.AddAsync(new LogGroup { Id = "original", Name = "Before" });
        var service = Service();
        await service.InitializeAsync();
        var first = service.Library!.Active;
        Assert.Equal("original", Assert.Single(service.Library.LocalViews[0].Definition.Groups).Id);
        await service.CreateAsync("Second");
        Assert.Empty(await _groups.GetAllAsync());
        await _groups.AddAsync(new LogGroup { Id = "second", Name = "Latest" });
        var second = service.Library!.Active;
        await service.ActivateAsync(first);
        Assert.Equal("original", Assert.Single(await _groups.GetAllAsync()).Id);
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal(2, restarted.Library!.LocalViews.Count);
        await restarted.ActivateAsync(second);
        Assert.Equal("Latest", Assert.Single(await _groups.GetAllAsync()).Name);
    }

    [Fact]
    public async Task CopyAndImport_RemapIdsAndPreserveOriginal()
    {
        await _groups.AddAsync(new LogGroup { Id = "root", Name = "Folder", Kind = LogGroupKind.Branch });
        await _groups.AddAsync(new LogGroup { Id = "child", Name = "Dashboard", ParentGroupId = "root" });
        var service = Service();
        await service.InitializeAsync();
        var first = service.Library!.Active;
        await service.CreateAsync("Copy", true);
        var copied = await _groups.GetAllAsync();
        Assert.DoesNotContain(copied, g => g.Id == "root" || g.Id == "child");
        Assert.Equal(copied.Single(g => g.Kind == LogGroupKind.Branch).Id, copied.Single(g => g.Kind == LogGroupKind.Dashboard).ParentGroupId);
        await service.CreateAsync("Imported", imported: new ViewExport());
        Assert.Equal(3, service.Library!.LocalViews.Count);
        await service.ActivateAsync(first);
        Assert.Contains(await _groups.GetAllAsync(), g => g.Id == "child");
    }

    [Theory]
    [InlineData("journal", 1)]
    [InlineData("journal", 2)]
    [InlineData("save", 1)]
    [InlineData("journal", 3)]
    public async Task ActivationFailure_RestoresOldView(string operation, int occurrence)
    {
        await _groups.AddAsync(new LogGroup { Id = "old", Name = "Original" });
        var service = Service();
        await service.InitializeAsync();
        var first = service.Library!.Active;
        _store.Fail(operation, occurrence);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync("New"));
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal(first, restarted.Library!.Active);
        Assert.Equal("old", Assert.Single(await _groups.GetAllAsync()).Id);
        Assert.Single(restarted.Library.LocalViews);
    }

    [Fact]
    public async Task GroupWriteFailure_RetainsJournalUntilStorageIsRepaired()
    {
        await _groups.AddAsync(new LogGroup { Id = "original", Name = "Original" });
        var service = Service();
        await service.InitializeAsync();
        var identity = service.Library!.Active;
        var path = Path.Combine(AppPaths.DataDirectory, "loggroups.json");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => service.CreateAsync("Cannot write"));
            Assert.True(service.NeedsRecovery);
            Assert.NotNull(await _store.LoadJournalAsync());
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal(identity, restarted.Library!.Active);
        Assert.Equal("original", Assert.Single(await _groups.GetAllAsync()).Id);
        Assert.Null(await _store.LoadJournalAsync());
    }

    [Fact]
    public async Task Startup_RemovesUnpromotedJournalTemporaryFile()
    {
        Directory.CreateDirectory(AppPaths.ViewsDirectory);
        var pending = Path.Combine(AppPaths.ViewsDirectory, JsonViewLibraryRepository.JournalFileName + ".tmp");
        await File.WriteAllTextAsync(pending, "incomplete");
        await Service().InitializeAsync();
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public async Task CommittedCleanupFailure_RestartFinishesNewView()
    {
        var service = Service();
        await service.InitializeAsync();
        _store.Fail("clear", 2);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync("Committed"));
        Assert.True(service.NeedsRecovery);
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal("Committed", restarted.Library!.LocalViews.Single(v => v.Id == restarted.Library.Active.ViewId).Name);
        Assert.Null(await _store.LoadJournalAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_RecoversInterruptedJournalIdempotently(bool committed)
    {
        var service = Service();
        await service.InitializeAsync();
        var before = ViewLibraryService.Copy(service.Library!);
        await service.CreateAsync("Target");
        var after = ViewLibraryService.Copy(service.Library!);
        await _store.SaveJournalAsync(new ViewActivationJournal { Before = before, After = after, Committed = committed });
        var restarted = Service();
        await restarted.InitializeAsync();
        await restarted.InitializeAsync();
        Assert.Equal(committed ? after.Active : before.Active, restarted.Library!.Active);
        Assert.Null(await _store.LoadJournalAsync());
    }

    [Fact]
    public async Task NamesAndDeletion_EnforceLibraryInvariants()
    {
        var service = Service();
        await service.InitializeAsync();
        var first = service.Library!.Active.ViewId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("default view"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(" "));
        await service.CreateAsync("Other");
        await service.RenameAsync(first, "Renamed");
        await service.DeleteAsync(first);
        Assert.Single(service.Library!.LocalViews);
    }

    [Fact]
    public async Task CorruptLibrary_IsPreserved()
    {
        Directory.CreateDirectory(AppPaths.ViewsDirectory);
        var path = Path.Combine(AppPaths.ViewsDirectory, "library.json");
        await File.WriteAllTextAsync(path, "{bad");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service().InitializeAsync());
        Assert.Equal("{bad", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FaultStore(IViewLibraryRepository inner) : IViewLibraryRepository
    {
        private string? _operation;
        private int _remaining;
        public void Fail(string operation, int occurrence) { _operation = operation; _remaining = occurrence; }
        private void Check(string operation)
        {
            if (_operation == operation && --_remaining == 0) { _operation = null; throw new IOException("Injected " + operation); }
        }
        public Task<ViewLibrary?> LoadAsync() => inner.LoadAsync();
        public Task SaveAsync(ViewLibrary value) { Check("save"); return inner.SaveAsync(value); }
        public Task<ViewActivationJournal?> LoadJournalAsync() => inner.LoadJournalAsync();
        public Task SaveJournalAsync(ViewActivationJournal value) { Check("journal"); return inner.SaveJournalAsync(value); }
        public Task ClearJournalAsync() { Check("clear"); return inner.ClearJournalAsync(); }
        public Task SaveSnapshotAsync(string id, ViewSourceSnapshot value) => inner.SaveSnapshotAsync(id, value);
        public Task<ViewSourceSnapshot> LoadSnapshotAsync(string id, string revision) => inner.LoadSnapshotAsync(id, revision);
        public Task RemoveSourceDataAsync(string id) => inner.RemoveSourceDataAsync(id);
    }
}
