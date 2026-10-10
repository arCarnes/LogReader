namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using LogReader.Mcp;

public sealed class McpContinuationSettingsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnconfiguredStorageKeepsProtocolInitializationAvailable(bool setupRequired)
    {
        var resolver = new UnconfiguredResolver(setupRequired);
        Assert.Equal(LogQueryEffectiveLimits.Default, await McpContinuationSettingsReader.ReadAsync(resolver));
    }

    private sealed class UnconfiguredResolver(bool setupRequired) : INonInteractiveStorageRootResolver
    {
        public string ResolveStorageRoot() => throw (setupRequired
            ? new StorageSetupRequiredException("Setup required", "selection.json", "storage")
            : new InstallConfigurationException("Configuration unavailable"));
    }

    [Fact]
    public async Task CustomTotalBudgetLimitsWorkingAdmission()
    {
        var config = new McpContinuationLimits { MaximumSessions = 3, MemoryPerQueryMiB = 2, TotalMemoryMiB = 60 };
        Assert.Null(config.Validate());
        using var store = new QueryContinuationStore(config.ToEffectiveLimits(), () => DateTimeOffset.UtcNow);
        using var first = await store.AcquireAsync(null, "search", "one", "catalog", default);
        var error = await Assert.ThrowsAsync<ContinuationException>(() => store.AcquireAsync(null, "count", "two", "catalog", default));
        Assert.Equal("memory_budget_exceeded", error.Reason);
    }

    [Fact]
    public void DefaultsMatchServerAndReservationMinimum()
    {
        var config = new McpContinuationLimits();
        Assert.Null(config.Validate());
        Assert.Equal(LogQueryEffectiveLimits.Default, config.ToEffectiveLimits());
        Assert.NotNull((config with { TotalMemoryMiB = 121 }).Validate());
        Assert.Null((config with { TotalMemoryMiB = 122 }).Validate());
        Assert.NotNull((config with { MaximumSessions = 0 }).Validate());
        Assert.NotNull((config with { MemoryPerQueryMiB = -1 }).Validate());
        Assert.NotNull((config with { TotalMemoryMiB = int.MaxValue, MemoryPerQueryMiB = int.MaxValue }).Validate());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"data\":{}}")]
    [InlineData("{\"mcpContinuationLimits\":null}")]
    public async Task LegacySettingsUseDefaultsWithoutRewriting(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            Assert.Equal(LogQueryEffectiveLimits.Default, await McpContinuationSettingsReader.ReadFileAsync(path));
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"data\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"data\":null}")]
    [InlineData("{\"mcpContinuationLimits\":{\"maximumSessions\":0}}")]
    [InlineData("{\"mcpContinuationLimits\":{\"totalMemoryMiB\":1}}")]
    public async Task InvalidConfigurationIsRejectedWithoutRewriting(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var error = await Record.ExceptionAsync(() => McpContinuationSettingsReader.ReadFileAsync(path));
            Assert.True(error is JsonException or InvalidDataException);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingAndOversizedFilesAreHandled()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        Assert.Equal(LogQueryEffectiveLimits.Default, await McpContinuationSettingsReader.ReadFileAsync(path));
        try
        {
            using (var stream = File.Create(path))
                stream.SetLength(PersistedDashboardSnapshotReader.DefaultMaximumStoreBytes + 1L);
            await Assert.ThrowsAsync<InvalidDataException>(() => McpContinuationSettingsReader.ReadFileAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PersistedLimitsReachStatusAndEnforcementAndAreSnapshotted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var scope = AppPaths.BeginTestScope(rootPath: root);
        try
        {
            var config = new McpContinuationLimits { MaximumSessions = 1, MemoryPerQueryMiB = 2, TotalMemoryMiB = 80 };
            var repo = new JsonSettingsRepository();
            await repo.SaveAsync(new AppSettings { McpContinuationLimits = config, LogFontSize = 14 });
            var path = Path.Combine(root, AppPaths.DataFolderName, "settings.json");
            var bytes = await File.ReadAllBytesAsync(path);
            var limits = await McpContinuationSettingsReader.ReadFileAsync(path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            using var backend = new OwnedHeadlessLogQueryBackend(limits);
            var status = await backend.GetStatusAsync();
            Assert.Equal(limits, status.Result!.Limits);
            using var store = new QueryContinuationStore(limits, () => DateTimeOffset.UtcNow);
            using (var lease = await store.AcquireAsync(null, "search", "one", "catalog", default))
            {
                var sizeError = Assert.Throws<ContinuationException>(() => lease.Commit("state", "reply", 3 * McpContinuationLimits.BytesPerMiB, false));
                Assert.Equal("query_too_large", sizeError.Reason);
                lease.Commit("state", "reply", 100, false);
            }
            var sessionError = await Assert.ThrowsAsync<ContinuationException>(() => store.AcquireAsync(null, "search", "two", "catalog", default));
            Assert.Equal("session_limit_exceeded", sessionError.Reason);
            await repo.SaveAsync(new AppSettings { McpContinuationLimits = config with { MaximumSessions = 3 } });
            Assert.Equal(1, (await backend.GetStatusAsync()).Result!.Limits.MaximumContinuationSessions);
            Assert.Equal(3, (await McpContinuationSettingsReader.ReadFileAsync(path)).MaximumContinuationSessions);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
