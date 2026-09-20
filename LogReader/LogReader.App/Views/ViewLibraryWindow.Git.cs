namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

internal sealed partial class ViewLibraryWindow
{
    private readonly IGitViewSourceClient _git = new GitViewSourceClient();
    private readonly ComboBox _revisionKind = new() { ItemsSource = Enum.GetValues<ViewRevisionKind>(), SelectedItem = ViewRevisionKind.Branch, Margin = new Thickness(4), Width = 110 };
    private readonly ComboBox _reference = new() { IsEditable = true, Margin = new Thickness(4), MinWidth = 250 };
    private ViewSourceRegistration? _pendingGit;
    private GitViewReferences? _references;

    partial void AddGitControls(StackPanel content)
    {
        content.Children.Add(new TextBlock { Text = "Git revision (blank branch uses remote default; tags/commits stay pinned)", Margin = new Thickness(4) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_revisionKind);
        row.Children.Add(_reference);
        content.Children.Add(row);
        _revisionKind.SelectionChanged += (_, _) =>
        {
            _reference.ItemsSource = _revisionKind.SelectedItem is ViewRevisionKind.Tag ? _references?.Tags :
                _revisionKind.SelectedItem is ViewRevisionKind.Branch ? _references?.Branches : null;
        };
    }

    partial void AddGitActions()
    {
        SourceAction("Load Git Refs", async () =>
        {
            var source = GitInput();
            _references = await _git.GetReferencesAsync(source, _operationCancellation!.Token);
            _reference.ItemsSource = source.RevisionKind == ViewRevisionKind.Tag ? _references.Tags : _references.Branches;
            if (string.IsNullOrWhiteSpace(_reference.Text) && source.RevisionKind == ViewRevisionKind.Branch)
                _reference.Text = _references.DefaultBranch ?? string.Empty;
        });
        SourceAction("Add Git Source", async () =>
        {
            var source = GitInput();
            source.Commit = null;
            await _viewModel.RunViewLibraryActionAsync(async library =>
            {
                var snapshot = await _git.ReadAsync(source, _operationCancellation!.Token);
                _operationCancellation.Token.ThrowIfCancellationRequested();
                await library.AcceptSourceAsync(source, snapshot);
            }, false);
            _pendingGit = null;
        });
        SourceAction("Change Revision", async () =>
        {
            var source = ViewLibraryService.Copy(SelectedSource());
            if (source.Kind != ViewSourceKind.Git) throw new InvalidOperationException("Select a Git source to change its revision.");
            source.RevisionKind = (ViewRevisionKind)_revisionKind.SelectedItem;
            source.Reference = _reference.Text.Trim();
            source.Commit = null;
            await RefreshSourceAsync(source);
        });
    }

    private ViewSourceRegistration GitInput()
    {
        if (_pendingGit?.Location != _location.Text.Trim())
            _pendingGit = new ViewSourceRegistration { Kind = ViewSourceKind.Git, Location = _location.Text.Trim() };
        _pendingGit.Name = _sourceName.Text.Trim();
        _pendingGit.RevisionKind = (ViewRevisionKind)_revisionKind.SelectedItem;
        _pendingGit.Reference = _reference.Text.Trim();
        return _pendingGit;
    }

    partial void SelectGitSource(ViewSourceRegistration source)
    {
        _revisionKind.SelectedItem = source.RevisionKind;
        _reference.Text = source.Reference;
    }

    private partial Task<ViewSourceSnapshot> ReadGitAsync(ViewSourceRegistration source, CancellationToken token)
        => _git.ReadAsync(source, token);
    private partial Task RemoveGitAsync(ViewSourceRegistration source)
        => source.Kind == ViewSourceKind.Git ? _git.RemoveCacheAsync(source.Id) : Task.CompletedTask;
}
