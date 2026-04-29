using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using LogReader.App.ViewModels;
using LogReader.Core.Models;
using LogReader.Testing;

namespace LogReader.Tests;

public class LogGroupViewModelTests
{
    [Fact]
    public async Task CommitEditAsync_WhenSaveSucceeds_AppliesRenameAfterPersistenceCompletes()
    {
        var model = new LogGroup
        {
            Id = "group-1",
            Name = "Original Name",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 3,
            ParentGroupId = "parent-1",
            FileIds = new List<string> { "file-1", "file-2" }
        };
        var saveStarted = new TaskCompletionSource<LogGroup>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSaveToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        LogGroup? savedGroup = null;
        var viewModel = new LogGroupViewModel(
            model,
            async group =>
            {
                savedGroup = group;
                saveStarted.TrySetResult(group);
                await allowSaveToComplete.Task;
            });
        viewModel.BeginEdit();
        viewModel.EditName = "Renamed Dashboard";

        var commitTask = viewModel.CommitEditAsync();
        var pendingGroup = await saveStarted.Task;

        Assert.NotSame(model, pendingGroup);
        Assert.Equal("Renamed Dashboard", pendingGroup.Name);
        Assert.Equal(model.Id, pendingGroup.Id);
        Assert.Equal(model.SortOrder, pendingGroup.SortOrder);
        Assert.Equal(model.ParentGroupId, pendingGroup.ParentGroupId);
        Assert.Equal(model.Kind, pendingGroup.Kind);
        Assert.Equal(model.FileIds, pendingGroup.FileIds);
        Assert.Equal("Original Name", viewModel.Name);
        Assert.Equal("Original Name", viewModel.Model.Name);
        Assert.True(viewModel.IsEditing);

        allowSaveToComplete.TrySetResult(true);
        await commitTask;

        Assert.Same(savedGroup, pendingGroup);
        Assert.Equal("Renamed Dashboard", viewModel.Name);
        Assert.Equal("Renamed Dashboard", viewModel.Model.Name);
        Assert.Equal("Renamed Dashboard", viewModel.EditName);
        Assert.False(viewModel.IsEditing);
    }

    [Fact]
    public async Task CommitEditAsync_WhenSaveFails_KeepsEditorOpenAndLeavesSavedNameUnchanged()
    {
        var model = new LogGroup
        {
            Id = "group-1",
            Name = "Original Name",
            Kind = LogGroupKind.Dashboard
        };
        LogGroup? savedGroup = null;
        var viewModel = new LogGroupViewModel(
            model,
            group =>
            {
                savedGroup = group;
                return Task.FromException(new IOException("Disk offline"));
            });
        viewModel.BeginEdit();
        viewModel.EditName = "Renamed Dashboard";

        var ex = await Assert.ThrowsAsync<IOException>(() => viewModel.CommitEditAsync());

        Assert.Equal("Disk offline", ex.Message);
        Assert.NotNull(savedGroup);
        Assert.NotSame(model, savedGroup);
        Assert.Equal("Renamed Dashboard", savedGroup!.Name);
        Assert.Equal("Original Name", viewModel.Name);
        Assert.Equal("Original Name", viewModel.Model.Name);
        Assert.Equal("Renamed Dashboard", viewModel.EditName);
        Assert.True(viewModel.IsEditing);
    }

    [Fact]
    public async Task CommitEditAsync_WhenEditNameIsBlank_CancelsEditWithoutSaving()
    {
        var saveCallCount = 0;
        var viewModel = new LogGroupViewModel(
            new LogGroup
            {
                Id = "group-1",
                Name = "Original Name",
                Kind = LogGroupKind.Dashboard
            },
            _ =>
            {
                saveCallCount++;
                return Task.CompletedTask;
            });
        viewModel.BeginEdit();
        viewModel.EditName = "   ";

        await viewModel.CommitEditAsync();

        Assert.Equal(0, saveCallCount);
        Assert.Equal("Original Name", viewModel.Name);
        Assert.Equal("Original Name", viewModel.EditName);
        Assert.False(viewModel.IsEditing);
    }

    [Fact]
    public void LayoutProperties_WhenDepthIsZero_KeepRootRowsClean()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(new Thickness(0, 0, 0, 0), viewModel.RowIndentMargin);
        Assert.Equal(new Thickness(22, 0, 0, 4), viewModel.MemberFilesMargin);
        Assert.Empty(viewModel.GuideRailMargins);
    }

    [Fact]
    public void LayoutProperties_WhenDepthIsNested_AlignFilesAndCreateOneRailPerAncestor()
    {
        var viewModel = CreateViewModel();
        viewModel.Depth = 3;

        Assert.Equal(new Thickness(45, 0, 0, 0), viewModel.RowIndentMargin);
        Assert.Equal(new Thickness(67, 0, 0, 4), viewModel.MemberFilesMargin);
        Assert.Equal(
            new[]
            {
                new Thickness(9, 0, 0, 0),
                new Thickness(24, 0, 0, 0),
                new Thickness(39, 0, 0, 0)
            },
            viewModel.GuideRailMargins);
    }

    [Fact]
    public void CanExpand_WhenDashboardHasPersistedFileIds_RemainsTrueWithoutMaterializedMemberRows()
    {
        var viewModel = new LogGroupViewModel(
            new LogGroup
            {
                Id = "group-1",
                Name = "Dashboard",
                Kind = LogGroupKind.Dashboard,
                FileIds = new List<string> { "file-1" }
            },
            _ => Task.CompletedTask);

        Assert.True(viewModel.CanExpand);
        Assert.Empty(viewModel.MemberFiles);
    }

    [Fact]
    public void ErrorAggregation_WhenSomeMemberFilesHaveErrors_ReportsPartialFailure()
    {
        var viewModel = CreateViewModel();

        viewModel.ReplaceMemberFiles(
            new[]
            {
                new GroupFileMemberViewModel("file-1", "good.log", @"C:\logs\good.log", showFullPath: false),
                new GroupFileMemberViewModel("file-2", "bad.log", @"C:\logs\bad.log", showFullPath: false, errorMessage: "File not found")
            });

        Assert.Equal(1, viewModel.ErroredMemberFileCount);
        Assert.True(viewModel.HasErroredMemberFiles);
        Assert.Equal("(1)", viewModel.ErrorCountTag);
    }

    [Fact]
    public void ErrorAggregation_WhenAllMemberFilesHaveErrors_ReportsCountWithoutChangingDisplayName()
    {
        var viewModel = CreateViewModel();

        viewModel.ReplaceMemberFiles(
            new[]
            {
                new GroupFileMemberViewModel("file-1", "a.log", @"C:\logs\a.log", showFullPath: false, errorMessage: "File not found"),
                new GroupFileMemberViewModel("file-2", "b.log", @"C:\logs\b.log", showFullPath: false, errorMessage: "File not found")
            });

        Assert.Equal(2, viewModel.ErroredMemberFileCount);
        Assert.True(viewModel.HasErroredMemberFiles);
        Assert.Equal("(2)", viewModel.ErrorCountTag);
        Assert.Equal("Dashboard", viewModel.DisplayName);
    }

    [Fact]
    public void ErrorAggregation_WhenErrorsClear_ResetsCountState()
    {
        var viewModel = CreateViewModel();

        viewModel.ReplaceMemberFiles(
            new[]
            {
                new GroupFileMemberViewModel("file-1", "bad.log", @"C:\logs\bad.log", showFullPath: false, errorMessage: "File not found")
            });

        viewModel.ReplaceMemberFiles(
            new[]
            {
                new GroupFileMemberViewModel("file-1", "good.log", @"C:\logs\good.log", showFullPath: false)
            });

        Assert.Equal(0, viewModel.ErroredMemberFileCount);
        Assert.False(viewModel.HasErroredMemberFiles);
        Assert.Equal("(0)", viewModel.ErrorCountTag);
    }

    [Fact]
    public void RefreshMemberFiles_ReplacesMembersWithSingleReset()
    {
        var viewModel = CreateViewModel();
        viewModel.Model.FileIds.AddRange(new[] { "file-1", "file-2", "file-3" });
        var collectionChanges = new List<NotifyCollectionChangedEventArgs>();
        viewModel.MemberFiles.CollectionChanged += (_, e) => collectionChanges.Add(e);

        viewModel.RefreshMemberFiles(
            Array.Empty<LogTabViewModel>(),
            new Dictionary<string, string>
            {
                ["file-1"] = @"C:\logs\one.log",
                ["file-2"] = @"C:\logs\two.log",
                ["file-3"] = @"C:\logs\three.log"
            },
            new Dictionary<string, bool>
            {
                ["file-1"] = true,
                ["file-2"] = false,
                ["file-3"] = true
            },
            selectedFileId: "file-3",
            showFullPath: false);

        var change = Assert.Single(collectionChanges);
        Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action);
        Assert.Equal(3, viewModel.MemberFiles.Count);
        Assert.Equal(1, viewModel.ErroredMemberFileCount);
        Assert.True(viewModel.MemberFiles.Single(member => member.FileId == "file-3").IsSelected);
    }

    [Theory]
    [InlineData(0, "{0:N0} bytes", 0d)]
    [InlineData(1023, "{0:N0} bytes", 1023d)]
    [InlineData(1024, "{0:N1} KB", 1d)]
    [InlineData(1048576, "{0:N1} MB", 1d)]
    [InlineData(1073741824, "{0:N1} GB", 1d)]
    public void FormatFileSize_UsesAdaptiveUnits(long bytes, string expectedFormat, double expectedValue)
    {
        var expected = string.Format(CultureInfo.CurrentCulture, expectedFormat, expectedValue);

        Assert.Equal(expected, GroupFileMemberViewModel.FormatFileSize(bytes));
    }

    [Fact]
    public void CreateFileSizeText_FormatsSizeOnly()
    {
        var fileSizeText = GroupFileMemberViewModel.CreateFileSizeText(12_345_678);

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", 11.8),
            fileSizeText);
    }

    [Theory]
    [InlineData(@"\\server-a\logs\app.log", "server-a")]
    [InlineData(@"C:\logs\app.log", null)]
    [InlineData(@"\\", null)]
    public void CreateHostNameText_FormatsUncHostOnly(string filePath, string? expected)
    {
        Assert.Equal(expected, GroupFileMemberViewModel.CreateHostNameText(filePath));
    }

    [Fact]
    public async Task RefreshMemberFiles_WhenTabIsOpen_IncludesFileSize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-stats-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "line 1\nline 2\n");
        var modified = new DateTime(2026, 4, 24, 14, 15, 0);
        File.SetLastWriteTime(path, modified);

        try
        {
            using var tab = new LogTabViewModel(
                "file-1",
                path,
                new StubLogReaderService(lineCount: 1234),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                new AppSettings());
            await tab.LoadAsync();
            var viewModel = CreateViewModel();
            viewModel.Model.FileIds.Add(tab.FileId);

            viewModel.RefreshMemberFiles(
                new[] { tab },
                new Dictionary<string, string> { [tab.FileId] = tab.FilePath },
                new Dictionary<string, bool> { [tab.FileId] = true },
                selectedFileId: null,
                showFullPath: false);

            var member = Assert.Single(viewModel.MemberFiles);
            Assert.True(member.HasFileSize);
            Assert.Equal(GroupFileMemberViewModel.CreateFileSizeText(tab), member.FileSizeText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefreshMemberFiles_WhenMemberIsNotOpen_DoesNotIncludeFileSize()
    {
        var viewModel = CreateViewModel();
        viewModel.Model.FileIds.Add("file-1");

        viewModel.RefreshMemberFiles(
            Array.Empty<LogTabViewModel>(),
            new Dictionary<string, string> { ["file-1"] = @"C:\logs\closed.log" },
            new Dictionary<string, bool> { ["file-1"] = true },
            selectedFileId: null,
            showFullPath: false);

        var member = Assert.Single(viewModel.MemberFiles);
        Assert.False(member.HasFileSize);
        Assert.Null(member.FileSizeText);
    }

    [Fact]
    public async Task RefreshMemberFile_WhenOpenTabSizeChanges_UpdatesFileSize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-stats-update-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "line 1\nline 2\n");

        try
        {
            using var tab = new LogTabViewModel(
                "file-1",
                path,
                new StubLogReaderService(lineCount: 2),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                new AppSettings());
            await tab.LoadAsync();
            var viewModel = CreateViewModel();
            viewModel.Model.FileIds.Add(tab.FileId);
            viewModel.RefreshMemberFiles(
                new[] { tab },
                new Dictionary<string, string> { [tab.FileId] = tab.FilePath },
                new Dictionary<string, bool> { [tab.FileId] = true },
                selectedFileId: null,
                showFullPath: false);
            var originalFileSize = Assert.Single(viewModel.MemberFiles).FileSizeText;

            using var updatedTab = new LogTabViewModel(
                "file-1",
                path,
                new StubLogReaderService(lineCount: 10),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                new AppSettings());
            await updatedTab.LoadAsync();
            viewModel.RefreshMemberFile(
                updatedTab.FileId,
                updatedTab,
                updatedTab.FilePath,
                fileExists: true,
                selectedFileId: null,
                showFullPath: false);

            var updatedMember = Assert.Single(viewModel.MemberFiles);
            Assert.NotEqual(originalFileSize, updatedMember.FileSizeText);
            Assert.Equal(GroupFileMemberViewModel.CreateFileSizeText(updatedTab), updatedMember.FileSizeText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LogGroupViewModel CreateViewModel()
    {
        return new LogGroupViewModel(
            new LogGroup
            {
                Id = "group-1",
                Name = "Dashboard",
                Kind = LogGroupKind.Dashboard
            },
            _ => Task.CompletedTask);
    }
}
