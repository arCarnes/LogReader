namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using LogReader.App.Services;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

internal sealed partial class ViewLibraryWindow
{
    private readonly ComboBox _sources = new() { DisplayMemberPath = nameof(ViewSourceRegistration.Name), Margin = new Thickness(4), MinWidth = 200 };
    private readonly TextBox _location = new() { Margin = new Thickness(4) };
    private readonly TextBox _sourceName = new() { Margin = new Thickness(4) };
    private readonly TextBlock _sourceDetails = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private readonly WrapPanel _sourceActions = new();
    private readonly ViewBundleReader _folderReader = new();

    partial void AddSources(StackPanel content)
    {
        content.Children.Add(new Separator { Margin = new Thickness(4, 16, 4, 8) });
        content.Children.Add(new TextBlock { Text = "Manage View Sources", FontSize = 18, Margin = new Thickness(4) });
        content.Children.Add(_sources);
        content.Children.Add(_sourceDetails);
        content.Children.Add(new TextBlock { Text = "Folder path or HTTPS / SSH Git remote (containing weeztail.json)", Margin = new Thickness(4) });
        content.Children.Add(_location);
        content.Children.Add(new TextBlock { Text = "Source display name (optional for a new source)", Margin = new Thickness(4) });
        content.Children.Add(_sourceName);
        AddGitControls(content);
        content.Children.Add(_sourceActions);
        SourceAction("Browse Folder", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select view source folder" };
            if (dialog.ShowDialog(this) == true) _location.Text = dialog.FolderName;
            return Task.CompletedTask;
        });
        SourceAction("Add Folder", async () =>
        {
            var source = new ViewSourceRegistration { Kind = ViewSourceKind.Folder, Location = _location.Text, Name = _sourceName.Text.Trim() };
            await _viewModel.RunViewLibraryActionAsync(async library =>
            {
                var snapshot = await _folderReader.ReadAsync(source, _operationCancellation!.Token);
                _operationCancellation.Token.ThrowIfCancellationRequested();
                await library.AcceptSourceAsync(source, snapshot);
            }, false);
        });
        AddGitActions();
        SourceAction("Refresh", async () =>
        {
            var source = SelectedSource();
            await RefreshSourceAsync(source);
        });
        SourceAction("Rename Source", () => _viewModel.RunViewLibraryActionAsync(l => l.RenameSourceAsync(SelectedSource().Id, _sourceName.Text), false));
        SourceAction("Remove Source", async () =>
        {
            var source = SelectedSource();
            if (MessageBox.Show(this, $"Remove '{source.Name}' from WeezTail? External files are preserved.", "Remove source", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await _viewModel.RunViewLibraryActionAsync(l => l.RemoveSourceAsync(source.Id), false);
            await RemoveGitCacheAsync(source);
        });
        var cancel = new Button { Content = "Cancel operation", Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
        cancel.Click += (_, _) => _operationCancellation?.Cancel();
        content.Children.Add(cancel);
        _sources.SelectionChanged += (_, _) =>
        {
            if (_sources.SelectedItem is not ViewSourceRegistration source) return;
            _sourceName.Text = source.Name;
            _location.Text = source.Location;
            _sourceDetails.Text = $"{source.Kind} • Last refreshed: {source.LastRefreshedAt?.ToLocalTime():g}" +
                (source.Kind == ViewSourceKind.Git ? $" • {source.RevisionKind}: {source.Reference} • {source.Commit?[..Math.Min(10, source.Commit.Length)]}" : "") +
                "\nRead-only. Refresh is manual; accepted views remain available offline.";
            SelectGitSource(source);
        };
        RefreshSources();
    }

    private ViewSourceRegistration SelectedSource() => _sources.SelectedItem as ViewSourceRegistration
        ?? throw new InvalidOperationException("Select a source first.");

    private Task RefreshSourceAsync(ViewSourceRegistration source)
    {
        source = ViewLibraryService.Copy(source);
        return _viewModel.RunViewLibraryActionAsync(async library =>
        {
            var snapshot = source.Kind == ViewSourceKind.Folder
                ? await _folderReader.ReadAsync(source, _operationCancellation!.Token)
                : await ReadGitSourceAsync(source, _operationCancellation!.Token);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            await library.AcceptSourceAsync(source, snapshot);
        }, _viewModel.ViewLibrary!.Library!.Active.SourceId == source.Id);
    }

    private void SourceAction(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) => await RunAsync(action);
        _sourceActions.Children.Add(button);
    }

    partial void RefreshSources()
    {
        if (_sources.ItemsSource is List<ViewSourceRegistration> previous && previous.SequenceEqual(_viewModel.ViewLibrary!.Library!.Sources)) return;
        var selected = (_sources.SelectedItem as ViewSourceRegistration)?.Id;
        _sources.ItemsSource = _viewModel.ViewLibrary!.Library!.Sources.ToList();
        _sources.SelectedItem = _viewModel.ViewLibrary.Library.Sources.FirstOrDefault(s => s.Id == selected) ??
            _viewModel.ViewLibrary.Library.Sources.FirstOrDefault(s => s.Id == _viewModel.ViewLibrary.Library.Active.SourceId) ??
            _viewModel.ViewLibrary.Library.Sources.FirstOrDefault();
    }

    partial void SetSourcesBusy(bool value)
    {
        _sourceActions.IsEnabled = !value;
        _sources.IsEnabled = _location.IsEnabled = _sourceName.IsEnabled = !value;
        _views.IsEnabled = _name.IsEnabled = !value;
        _revisionKind.IsEnabled = _reference.IsEnabled = !value;
    }
    partial void AddGitControls(StackPanel content);
    partial void AddGitActions();
    partial void SelectGitSource(ViewSourceRegistration source);
    private Task<ViewSourceSnapshot> ReadGitSourceAsync(ViewSourceRegistration source, CancellationToken token) => ReadGitAsync(source, token);
    private Task RemoveGitCacheAsync(ViewSourceRegistration source) => RemoveGitAsync(source);
    private partial Task<ViewSourceSnapshot> ReadGitAsync(ViewSourceRegistration source, CancellationToken token);
    private partial Task RemoveGitAsync(ViewSourceRegistration source);
}
