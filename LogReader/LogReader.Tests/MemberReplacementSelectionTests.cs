using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public sealed class MemberReplacementSelectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Replacement_PreservesSelectionByIdAndSurvivingRangeAnchor(bool initialFullPath, bool changePath)
    {
        var group = CreateGroup();
        var first = CreateRow("file", initialFullPath);
        var selected = CreateRow("FILE", initialFullPath);
        group.ReconcileMemberFiles(new[] { first, selected });
        group.SelectOnlyBatchMemberFile(selected);
        var other = CreateGroup();
        other.ReconcileMemberFiles(new[] { CreateRow("FILE", initialFullPath) });

        var replacement = new GroupFileMemberViewModel(selected.FileId, "changed.log",
            changePath ? @"C:\logs\changed.log" : selected.FilePath, changePath ? initialFullPath : !initialFullPath);
        group.ReconcileMemberFiles(new[] { CreateRow("file", replacement.ShowFullPath), replacement, CreateRow("new", replacement.ShowFullPath) });
        Assert.Same(replacement, group.MemberFiles[1]);
        Assert.True(replacement.IsBatchSelected);
        Assert.False(group.MemberFiles[0].IsBatchSelected);
        Assert.False(group.MemberFiles[2].IsBatchSelected);
        Assert.False(Assert.Single(other.MemberFiles).IsBatchSelected);
        Assert.Equal(1, group.BatchSelectedMemberFileCount);
        group.SelectBatchMemberFileRange(group.MemberFiles[2]);
        Assert.Equal(new[] { "FILE", "new" }, group.GetBatchSelectedMemberFiles().Select(row => row.FileId));
    }

    [Fact]
    public void Replacement_DeletedSelectionAndAnchorDoNotSelectNewIds()
    {
        var group = CreateGroup();
        var selected = CreateRow("removed", false);
        group.ReconcileMemberFiles(new[] { selected, CreateRow("survivor", false) });
        group.SelectOnlyBatchMemberFile(selected);
        group.ReconcileMemberFiles(new[] { CreateRow("survivor", true), CreateRow("new", true) });
        Assert.All(group.MemberFiles, row => Assert.False(row.IsBatchSelected));
        group.SelectBatchMemberFileRange(group.MemberFiles[1]);
        Assert.Equal("new", Assert.Single(group.GetBatchSelectedMemberFiles()).FileId);
    }

    private static LogGroupViewModel CreateGroup()
        => new(new LogGroup { Kind = LogGroupKind.Dashboard }, _ => Task.CompletedTask);

    private static GroupFileMemberViewModel CreateRow(string id, bool showFullPath)
        => new(id, id + ".log", $@"C:\logs\{id}.log", showFullPath);
}
