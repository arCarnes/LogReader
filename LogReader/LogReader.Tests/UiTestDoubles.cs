namespace LogReader.Tests;

using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class StubFileDialogService : IFileDialogService
{
    public Func<OpenFileDialogRequest, OpenFileDialogResult> OnShowOpenFileDialog { get; set; }
        = static _ => new OpenFileDialogResult(false, Array.Empty<string>());

    public Func<SaveFileDialogRequest, SaveFileDialogResult> OnShowSaveFileDialog { get; set; }
        = static _ => new SaveFileDialogResult(false, null);

    public OpenFileDialogRequest? LastOpenRequest { get; private set; }

    public SaveFileDialogRequest? LastSaveRequest { get; private set; }

    public OpenFileDialogResult ShowOpenFileDialog(OpenFileDialogRequest request)
    {
        LastOpenRequest = request;
        return OnShowOpenFileDialog(request);
    }

    public SaveFileDialogResult ShowSaveFileDialog(SaveFileDialogRequest request)
    {
        LastSaveRequest = request;
        return OnShowSaveFileDialog(request);
    }
}

internal sealed class StubFolderDialogService : IFolderDialogService
{
    public Func<FolderDialogRequest, FolderDialogResult> OnShowFolderDialog { get; set; }
        = static _ => new FolderDialogResult(false, null);

    public FolderDialogRequest? LastRequest { get; private set; }

    public FolderDialogResult ShowFolderDialog(FolderDialogRequest request)
    {
        LastRequest = request;
        return OnShowFolderDialog(request);
    }
}

internal sealed class StubMessageBoxService : IMessageBoxService
{
    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> OnShow { get; set; }
        = static (_, _, _, _) => MessageBoxResult.None;

    public Func<Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> OnShowWithOwner { get; set; }
        = static (_, _, _, _, _) => MessageBoxResult.None;

    public string? LastMessage { get; private set; }

    public string? LastCaption { get; private set; }

    public MessageBoxButton? LastButtons { get; private set; }

    public MessageBoxImage? LastImage { get; private set; }

    public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        LastMessage = message;
        LastCaption = caption;
        LastButtons = buttons;
        LastImage = image;
        return OnShow(message, caption, buttons, image);
    }

    public MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        LastMessage = message;
        LastCaption = caption;
        LastButtons = buttons;
        LastImage = image;
        return OnShowWithOwner(owner, message, caption, buttons, image);
    }
}

internal sealed class StubSettingsDialogService : ISettingsDialogService
{
    public Func<SettingsViewModel, Window?, bool> OnShowDialog { get; set; } = static (_, _) => false;

    public SettingsViewModel? LastViewModel { get; private set; }

    public Window? LastOwner { get; private set; }

    public bool ShowDialog(SettingsViewModel viewModel, Window? owner)
    {
        LastViewModel = viewModel;
        LastOwner = owner;
        return OnShowDialog(viewModel, owner);
    }
}

internal sealed class StubBulkOpenPathsDialogService : IBulkOpenPathsDialogService
{
    public Func<BulkOpenPathsDialogRequest, BulkOpenPathsDialogResult> OnShowDialog { get; set; }
        = static _ => new BulkOpenPathsDialogResult(false, string.Empty);

    public BulkOpenPathsDialogRequest? LastRequest { get; private set; }

    public BulkOpenPathsDialogResult ShowDialog(BulkOpenPathsDialogRequest request)
    {
        LastRequest = request;
        return OnShowDialog(request);
    }
}

internal sealed class StubPatternManagerDialogService : IPatternManagerDialogService
{
    public Func<PatternManagerViewModel, Window?, bool> OnShowDialog { get; set; } = static (_, _) => false;

    public PatternManagerViewModel? LastViewModel { get; private set; }

    public Window? LastOwner { get; private set; }

    public bool ShowDialog(PatternManagerViewModel viewModel, Window? owner)
    {
        LastViewModel = viewModel;
        LastOwner = owner;
        return OnShowDialog(viewModel, owner);
    }
}

internal sealed class StubReplacementPatternRepository : IReplacementPatternRepository
{
    public Func<Task<List<ReplacementPattern>>> OnLoadAsync { get; set; }
        = static () => Task.FromResult(new List<ReplacementPattern>());

    public Func<List<ReplacementPattern>, Task> OnSaveAsync { get; set; }
        = static _ => Task.CompletedTask;

    public int LoadCallCount { get; private set; }

    public int SaveCallCount { get; private set; }

    public List<ReplacementPattern>? LastSavedPatterns { get; private set; }

    public async Task<List<ReplacementPattern>> LoadAsync()
    {
        LoadCallCount++;
        return await OnLoadAsync();
    }

    public async Task SaveAsync(List<ReplacementPattern> patterns)
    {
        SaveCallCount++;
        LastSavedPatterns = patterns.Select(pattern => new ReplacementPattern
        {
            Id = pattern.Id,
            Name = pattern.Name,
            FindPattern = pattern.FindPattern,
            ReplacePattern = pattern.ReplacePattern
        }).ToList();

        await OnSaveAsync(patterns);
    }
}

internal sealed class StubStorageSetupDialogService : IStorageSetupDialogService
{
    public Func<StorageSetupViewModel, bool> OnShowDialog { get; set; } = static _ => false;

    public StorageSetupViewModel? LastViewModel { get; private set; }

    public bool ShowDialog(StorageSetupViewModel viewModel)
    {
        LastViewModel = viewModel;
        return OnShowDialog(viewModel);
    }
}
