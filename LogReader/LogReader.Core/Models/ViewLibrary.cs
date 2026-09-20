namespace LogReader.Core.Models;

public sealed record ViewIdentity(string SourceId, string ViewId)
{
    public const string LocalSourceId = "local";
    public bool IsLocal => SourceId == LocalSourceId;
}

public sealed class SavedView
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ViewExport Definition { get; set; } = new();
}

public enum ViewSourceKind { Folder, Git }
public enum ViewRevisionKind { Branch, Tag, Commit }

public sealed class ViewSourceRegistration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ViewSourceKind Kind { get; set; }
    public string Location { get; set; } = string.Empty;
    public ViewRevisionKind RevisionKind { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string? Commit { get; set; }
    public string? AcceptedRevision { get; set; }
    public string? PreviousRevision { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public Dictionary<string, Dictionary<string, string>> GroupIds { get; set; } = new();
}

public sealed class ViewSourceSnapshot
{
    public string Name { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string? Commit { get; set; }
    public List<SavedView> Views { get; set; } = new();
}

public sealed class ViewLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public ViewIdentity Active { get; set; } = new(ViewIdentity.LocalSourceId, string.Empty);
    public List<SavedView> LocalViews { get; set; } = new();
    public List<ViewSourceRegistration> Sources { get; set; } = new();
}

public sealed class ViewActivationJournal
{
    public int SchemaVersion { get; set; } = 1;
    public bool Committed { get; set; }
    public ViewLibrary Before { get; set; } = new();
    public ViewLibrary After { get; set; } = new();
    public List<LogGroup> BeforeGroups { get; set; } = new();
    public List<LogGroup> AfterGroups { get; set; } = new();
}
