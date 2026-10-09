namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.App.Services;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Testing;

public sealed class UiStateInitializationTests
{
    [Fact]
    public async Task Restart_RestoresExpansionWithoutStartupSave_AndFlushesFinalState()
    {
        var groups = new StubLogGroupRepository();
        await groups.AddAsync(new LogGroup { Id = "folder", Kind = LogGroupKind.Branch, Name = "Folder" });
        await groups.AddAsync(new LogGroup { Id = "dashboard", ParentGroupId = "folder", Name = "Dashboard" });
        var ui = new UiStatePersistenceTests.RecordingRepository
        {
            Loaded = new UiState { GroupExpansionById = new() { ["folder"] = true, ["dashboard"] = false } }
        };
        using (var first = Create(groups, ui))
        {
            await first.InitializeAsync();
            Assert.True(first.Groups[0].IsExpanded);
            Assert.False(first.Groups[1].IsExpanded);
            Assert.Empty(ui.Saves);
            first.Groups[1].IsExpanded = true;
            first.BeginShutdown();
            await first.FlushUiStateAsync();
        }
        Assert.True(Assert.Single(ui.Saves).GroupExpansionById["dashboard"]);
        using var restarted = Create(groups, ui);
        await restarted.InitializeAsync();
        Assert.All(restarted.Groups, group => Assert.True(group.IsExpanded));
        Assert.Single(ui.Saves);
    }

    [Fact]
    public async Task UnreadableState_DoesNotPreventInitializationOrOverwriteAtStartup()
    {
        var groups = new StubLogGroupRepository();
        await groups.AddAsync(new LogGroup { Id = "dashboard", Name = "Dashboard" });
        var ui = new UiStatePersistenceTests.RecordingRepository { LoadFailure = new JsonException("invalid") };
        var notices = 0;
        var messages = new StubMessageBoxService
        {
            OnShow = (_, _, _, _) => { notices++; return MessageBoxResult.OK; }
        };
        using var vm = Create(groups, ui, messages);
        await vm.InitializeAsync();
        Assert.False(Assert.Single(vm.Groups).IsExpanded);
        Assert.Empty(ui.Saves);
        Assert.Equal(1, notices);
        vm.BeginShutdown();
        await vm.FlushUiStateAsync();
        Assert.Single(ui.Saves);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task WorkerSave_UsesBackgroundAndReportsFailureOnDispatcher()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var groups = new StubLogGroupRepository();
            await groups.AddAsync(new LogGroup { Id = "dashboard", Name = "Dashboard" });
            var ui = new UiStatePersistenceTests.RecordingRepository();
            ui.OnSave = _ =>
            {
                Assert.False(dispatcher.CheckAccess());
                return Task.FromException(new IOException("locked"));
            };
            var noticed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notices = 0;
            var messages = new StubMessageBoxService
            {
                OnShow = (_, caption, _, _) =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    Assert.Equal("UI state", caption);
                    notices++;
                    noticed.TrySetResult();
                    return MessageBoxResult.OK;
                }
            };
            using var vm = Create(groups, ui, messages);
            await vm.InitializeAsync();
            vm.Groups[0].IsExpanded = true;
            await noticed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.Groups[0].IsExpanded);
            vm.BeginShutdown();
            await vm.FlushUiStateAsync();
            Assert.Equal(1, notices);
            Assert.Equal(2, ui.Saves.Count);
        });
    }

    private static MainViewModel Create(ILogGroupRepository groups, IUiStateRepository ui, IMessageBoxService? messages = null)
        => TestMainViewModelFactory.Create(new StubLogFileRepository(), groups, new StubSettingsRepository(),
            new StubLogReaderService(), new StubSearchService(), new StubFileTailService(),
            new FileEncodingDetectionService(), uiStateRepository: ui, messageBoxService: messages);
}
