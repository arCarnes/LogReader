namespace LogReader.Tests;

using System.Windows;
using LogReader.Core.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;

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
    public Func<SettingsViewModel, bool> OnShowDialog { get; set; } = static _ => false;

    public SettingsViewModel? LastViewModel { get; private set; }

    public bool ShowDialog(SettingsViewModel viewModel)
    {
        LastViewModel = viewModel;
        return OnShowDialog(viewModel);
    }
}

internal sealed class StubWindowOwnerProvider : IWindowOwnerProvider
{
    public Window? Owner { get; set; }

    public int CallCount { get; private set; }

    public Window? GetOwner()
    {
        CallCount++;
        return Owner;
    }
}

internal sealed class StubSettingsDialogWindow : ISettingsDialogWindow
{
    private object? _dataContext;
    private Window? _owner;

    public List<string> Events { get; } = new();

    public int ShowDialogCallCount { get; private set; }

    public bool? ShowDialogResult { get; set; } = true;

    public object? DataContextAtShowDialog { get; private set; }

    public Window? OwnerAtShowDialog { get; private set; }

    public object? DataContext
    {
        get => _dataContext;
        set
        {
            _dataContext = value;
            Events.Add("DataContext");
        }
    }

    public Window? Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            Events.Add("Owner");
        }
    }

    public bool? ShowDialog()
    {
        ShowDialogCallCount++;
        DataContextAtShowDialog = _dataContext;
        OwnerAtShowDialog = _owner;
        Events.Add("ShowDialog");
        return ShowDialogResult;
    }
}

internal sealed class StubSettingsDialogWindowFactory : ISettingsDialogWindowFactory
{
    public StubSettingsDialogWindow Window { get; set; } = new();

    public int CreateCallCount { get; private set; }

    public ISettingsDialogWindow Create()
    {
        CreateCallCount++;
        return Window;
    }
}

internal sealed class StubLogAppearanceService : ILogAppearanceService
{
    public int ApplyCallCount { get; private set; }

    public AppSettings? LastSettings { get; private set; }

    public void Apply(AppSettings settings)
    {
        ApplyCallCount++;
        LastSettings = settings;
    }
}

internal sealed class StubTabLifecycleScheduler : ITabLifecycleScheduler
{
    private readonly StubRegistration _registration = new();

    public int ScheduleCallCount { get; private set; }

    public TimeSpan? LastDueTime { get; private set; }

    public TimeSpan? LastInterval { get; private set; }

    public Action? LastCallback { get; private set; }

    public int DisposeCallCount => _registration.DisposeCallCount;

    public IDisposable ScheduleRecurring(TimeSpan dueTime, TimeSpan interval, Action callback)
    {
        ScheduleCallCount++;
        LastDueTime = dueTime;
        LastInterval = interval;
        LastCallback = callback;
        return _registration;
    }

    public void RunScheduledCallback() => LastCallback?.Invoke();

    private sealed class StubRegistration : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose()
        {
            DisposeCallCount++;
        }
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
