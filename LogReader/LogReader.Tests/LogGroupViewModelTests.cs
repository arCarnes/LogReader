using System.IO;
using System.Windows;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

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
        Assert.True(viewModel.HasMemberErrors);
        Assert.False(viewModel.HasOnlyErroredMembers);
    }

    [Fact]
    public void ErrorAggregation_WhenAllMemberFilesHaveErrors_ReportsFullFailure()
    {
        var viewModel = CreateViewModel();

        viewModel.ReplaceMemberFiles(
            new[]
            {
                new GroupFileMemberViewModel("file-1", "a.log", @"C:\logs\a.log", showFullPath: false, errorMessage: "File not found"),
                new GroupFileMemberViewModel("file-2", "b.log", @"C:\logs\b.log", showFullPath: false, errorMessage: "File not found")
            });

        Assert.Equal(2, viewModel.ErroredMemberFileCount);
        Assert.True(viewModel.HasMemberErrors);
        Assert.True(viewModel.HasOnlyErroredMembers);
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
