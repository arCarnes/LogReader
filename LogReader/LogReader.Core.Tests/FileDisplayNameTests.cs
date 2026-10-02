namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class FileDisplayNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WeezTailNames_{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public FileDisplayNameTests()
    {
        Directory.CreateDirectory(_root);
        _scope = AppPaths.BeginTestScope(rootPath: _root);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Production API  ", "Production API")]
    public void Normalize_TrimsOrResets(string? input, string? expected)
        => Assert.Equal(expected, LogFileDisplayName.Normalize(input));

    [Theory]
    [InlineData("API\nworker")]
    [InlineData("API\tworker")]
    [InlineData("API\u2028worker")]
    public void Normalize_RejectsMultilineOrControlText(string input)
        => Assert.Throws<ArgumentException>(() => LogFileDisplayName.Normalize(input));

    [Fact]
    public async Task Repository_NamesPreserveIdentityPathAndConcurrentOpenTimestamp()
    {
        var repo = new JsonLogFileRepository();
        var path = Path.Combine(_root, "app.log");
        var entry = await repo.GetOrCreateByPathAsync(path);
        var openedAt = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        await Task.WhenAll(
            repo.UpdateDisplayNamesAsync(new Dictionary<string, string?> { [entry.Id] = "  API  " }),
            repo.GetOrCreateByPathAsync(path, openedAt));
        var saved = Assert.Single(await new JsonLogFileRepository().GetAllAsync());
        Assert.Equal(entry.Id, saved.Id);
        Assert.Equal(path, saved.FilePath);
        Assert.Equal(openedAt, saved.LastOpenedAt);
        Assert.Equal("API", saved.DisplayName);
        await repo.UpdateDisplayNamesAsync(new Dictionary<string, string?> { [entry.Id] = " " });
        Assert.Null(Assert.Single(await repo.GetAllAsync()).DisplayName);
    }

    [Fact]
    public async Task Repository_UnknownIdRejectsWholeBatch()
    {
        var repo = new JsonLogFileRepository();
        var entry = await repo.GetOrCreateByPathAsync(Path.Combine(_root, "app.log"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repo.UpdateDisplayNamesAsync(
            new Dictionary<string, string?> { [entry.Id] = "API", ["unknown"] = "Other" }));
        Assert.Null(Assert.Single(await repo.GetAllAsync()).DisplayName);
    }

    [Fact]
    public async Task Repository_FailedWritePreservesPreviousName()
    {
        var repo = new JsonLogFileRepository();
        var entry = await repo.GetOrCreateByPathAsync(Path.Combine(_root, "app.log"));
        var tempPath = JsonStore.GetFilePath("logfiles.json") + ".tmp";
        Directory.CreateDirectory(tempPath);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repo.UpdateDisplayNamesAsync(
            new Dictionary<string, string?> { [entry.Id] = "API" }));
        Assert.Null(Assert.Single(await repo.GetAllAsync()).DisplayName);
    }

    [Fact]
    public async Task SnapshotTreeAndSelection_ShareNameAndStableId()
    {
        var repo = new JsonLogFileRepository();
        var entry = await repo.GetOrCreateByPathAsync(Path.Combine(_root, "app.log"));
        var groups = new JsonLogGroupRepository(repo);
        await groups.AddAsync(new LogGroup { Id = "one", Name = "One", FileIds = [entry.Id] });
        await groups.AddAsync(new LogGroup { Id = "two", Name = "Two", FileIds = [entry.Id] });
        using var reader = new PersistedDashboardSnapshotReader(new NameTestRootResolver(_root),
            new PersistedSnapshotFileSystem(), PersistedDashboardSnapshotReaderOptions.Default);
        var before = (await reader.ReadAsync()).Snapshot!;
        await repo.UpdateDisplayNamesAsync(new Dictionary<string, string?> { [entry.Id] = "Production API" });
        var after = (await reader.ReadAsync()).Snapshot!;
        Assert.NotEqual(before.Revision, after.Revision);
        var nodes = new ConfiguredLogTreeProjector().Project(after, new()).Nodes.Where(node => node.Id == entry.Id).ToList();
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, node => Assert.Equal("Production API", node.DisplayName));
        var selected = new DashboardSelectionResolver().Resolve(after,
            new ConfiguredLogSelectionRequest([new(ConfiguredLogTargetKind.LogFile, entry.Id)], new DateOnly(2026, 10, 2)));
        var file = Assert.Single(selected.Files);
        Assert.Equal(entry.Id, file.FileId);
        Assert.Equal(entry.FilePath, file.PhysicalPath);
        Assert.Equal("Production API", file.DisplayName);
        Assert.All(file.Provenance, provenance => Assert.EndsWith(" / Production API", provenance.TargetTreePath));
        await repo.UpdateDisplayNamesAsync(new Dictionary<string, string?> { [entry.Id] = null });
        Assert.Equal(before.Revision, (await reader.ReadAsync()).Snapshot!.Revision);
    }

    [Fact]
    public async Task ViewExport_PreservesNamesAndExplicitReset()
    {
        var repo = new JsonLogFileRepository();
        var named = await repo.GetOrCreateByPathAsync(Path.Combine(_root, "app.log"));
        var unnamed = await repo.GetOrCreateByPathAsync(Path.Combine(_root, "worker.log"));
        await repo.UpdateDisplayNamesAsync(new Dictionary<string, string?> { [named.Id] = "API" });
        var groups = new JsonLogGroupRepository(repo);
        await groups.AddAsync(new LogGroup { Name = "Production", FileIds = [named.Id, unnamed.Id] });
        var path = Path.Combine(_root, "view.json");
        await groups.ExportViewAsync(path);
        var imported = (await groups.ImportViewAsync(path))!;
        Assert.Equal(2, imported.SchemaVersion);
        Assert.Equal("API", imported.FileDisplayNames[named.FilePath]);
        Assert.Null(imported.FileDisplayNames[unnamed.FilePath]);
    }

    [Fact]
    public async Task ViewImport_RejectsDuplicateEquivalentNameKeysBeforeMutation()
    {
        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());
        var path = Path.Combine(_root, "view.json");
        await File.WriteAllTextAsync(path, """
            { "schemaVersion": 2, "groups": [], "fileDisplayNames": { "app.log": "A", "APP.log": "B" } }
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repo.ImportViewAsync(path));
    }

    [Theory]
    [InlineData("app.log", "API\nWorker")]
    [InlineData("other.log", "Other")]
    public void ViewImport_InvalidOrUnreferencedNamesAreRejected(string path, string name)
    {
        var view = new ViewExport
        {
            Groups = [new ViewExportGroup { Name = "Production", FilePaths = ["app.log"] }],
            FileDisplayNames = new() { [path] = name }
        };
        Assert.Throws<InvalidDataException>(() => DashboardTopologyValidator.ValidateImportedView(view));
    }

    private sealed class NameTestRootResolver(string root) : INonInteractiveStorageRootResolver
    {
        public string ResolveStorageRoot() => root;
    }

    public void Dispose()
    {
        _scope.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
