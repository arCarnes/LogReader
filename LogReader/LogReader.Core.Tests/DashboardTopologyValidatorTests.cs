namespace LogReader.Core.Tests;

using LogReader.Core;
using LogReader.Core.Models;

public class DashboardTopologyValidatorTests
{
    [Fact]
    public void ValidatePersistedGroups_DuplicateIds_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DashboardTopologyValidator.ValidatePersistedGroups(
            new List<LogGroup>
            {
                new() { Id = "duplicate", Name = "First", Kind = LogGroupKind.Dashboard },
                new() { Id = "duplicate", Name = "Second", Kind = LogGroupKind.Dashboard }
            }));

        Assert.Contains("duplicate group ID", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePersistedGroups_MissingParent_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DashboardTopologyValidator.ValidatePersistedGroups(
            new List<LogGroup>
            {
                new()
                {
                    Id = "dashboard-1",
                    Name = "Orphan",
                    Kind = LogGroupKind.Dashboard,
                    ParentGroupId = "missing-parent"
                }
            }));

        Assert.Contains("missing parent group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePersistedGroups_DashboardWithChild_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DashboardTopologyValidator.ValidatePersistedGroups(
            new List<LogGroup>
            {
                new() { Id = "dashboard-1", Name = "Parent", Kind = LogGroupKind.Dashboard },
                new()
                {
                    Id = "dashboard-2",
                    Name = "Child",
                    Kind = LogGroupKind.Dashboard,
                    ParentGroupId = "dashboard-1"
                }
            }));

        Assert.Contains("cannot have child groups", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePersistedGroups_BranchWithFiles_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DashboardTopologyValidator.ValidatePersistedGroups(
            new List<LogGroup>
            {
                new()
                {
                    Id = "branch-1",
                    Name = "Folder",
                    Kind = LogGroupKind.Branch,
                    FileIds = new List<string> { "file-1" }
                }
            }));

        Assert.Contains("cannot own file IDs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateImportedView_BlankFilePath_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DashboardTopologyValidator.ValidateImportedView(
            new ViewExport
            {
                Groups = new List<ViewExportGroup>
                {
                    new()
                    {
                        Id = "dashboard-1",
                        Name = "Imported Dashboard",
                        Kind = LogGroupKind.Dashboard,
                        FilePaths = new List<string> { @"C:\logs\ok.log", " " }
                    }
                }
            }));

        Assert.Contains("blank file paths", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
