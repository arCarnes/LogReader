namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;

internal interface IAppBootstrapper
{
    Task<AppComposition> CreateInitializedAsync(bool enableLifecycleTimer = true);
}

internal sealed class AppBootstrapper : IAppBootstrapper
{
    private readonly Func<bool, Task<AppComposition>> _createInitializedAsync;

    public AppBootstrapper()
        : this(new AppCompositionBuilder())
    {
    }

    internal AppBootstrapper(IAppCompositionBuilder compositionBuilder)
        : this(enableLifecycleTimer => CreateInitializedAsync(compositionBuilder, enableLifecycleTimer))
    {
    }

    internal AppBootstrapper(Func<bool, Task<AppComposition>> createInitializedAsync)
    {
        _createInitializedAsync = createInitializedAsync;
    }

    public Task<AppComposition> CreateInitializedAsync(bool enableLifecycleTimer = true)
        => _createInitializedAsync(enableLifecycleTimer);

    private static async Task<AppComposition> CreateInitializedAsync(
        IAppCompositionBuilder compositionBuilder,
        bool enableLifecycleTimer)
    {
        var composition = compositionBuilder.Build(enableLifecycleTimer);

        try
        {
            await composition.MainViewModel.InitializeAsync();
            return composition;
        }
        catch
        {
            App.CleanupFailedStartup((IAppWindow?)null, composition.MainViewModel, composition.TailService);
            throw;
        }
    }
}

internal sealed record AppComposition(MainViewModel MainViewModel, IFileTailService TailService);
