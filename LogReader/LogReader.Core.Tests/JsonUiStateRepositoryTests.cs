namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class JsonUiStateRepositoryTests : IAsyncLifetime
{
    private string _directory = null!;
    private string StorePath => Path.Combine(_directory, "Data", "ui-state.json");

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "WeezTailUiState_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        JsonStore.SetBasePathForTests(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        JsonStore.SetBasePathForTests(null);
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MissingStore_ReturnsEmptyWithoutWriting()
    {
        Assert.Empty((await new JsonUiStateRepository().LoadAsync()).GroupExpansionById);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task RoundTripAndReplacement_PreserveBothStatesAndEnvelope()
    {
        var repository = new JsonUiStateRepository();
        await repository.SaveAsync(new UiState { GroupExpansionById = new() { ["folder"] = true, ["empty"] = false } });
        var loaded = await repository.LoadAsync();
        Assert.True(loaded.GroupExpansionById["folder"]);
        Assert.False(loaded.GroupExpansionById["empty"]);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("data").GetProperty("groupExpansionById").GetProperty("folder").GetBoolean());
        await repository.SaveAsync(new UiState { GroupExpansionById = new() { ["folder"] = false } });
        Assert.False((await repository.LoadAsync()).GroupExpansionById["folder"]);
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":2,\"data\":{\"groupExpansionById\":{}}}")]
    [InlineData("{\"schemaVersion\":1,\"data\":{\"groupExpansionById\":null}}")]
    [InlineData("{\"schemaVersion\":1,\"data\":{\"groupExpansionById\":{\"id\":\"yes\"}}}")]
    public async Task InvalidStore_ThrowsWithoutChangingFile(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, contents);
        await Assert.ThrowsAnyAsync<JsonException>(() => new JsonUiStateRepository().LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task FailedReplacement_PreservesPreviousStore()
    {
        var repository = new JsonUiStateRepository();
        await repository.SaveAsync(new UiState { GroupExpansionById = new() { ["id"] = true } });
        Directory.CreateDirectory(StorePath + ".tmp");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.SaveAsync(new UiState()));
        Assert.True((await repository.LoadAsync()).GroupExpansionById["id"]);
    }
}
