namespace LogReader.App.Services;

using LogReader.Core.Interfaces;

internal interface IWorkspaceServicesFactory
{
    // Construction only: do not invoke host operations or start background work here.
    WorkspaceServices Create(ITabWorkspaceHost tabHost, IDashboardWorkspaceHost dashboardHost);
}

internal sealed class WorkspaceServicesFactory(
    ILogFileRepository fileRepository,
    ILogGroupRepository groupRepository,
    ILogReaderService logReader,
    IFileTailService tailService,
    IEncodingDetectionService encodingDetection,
    IUiDispatcher dispatcher,
    LogFileCatalogService? catalog = null,
    TabWorkspaceService? tabWorkspace = null,
    DashboardWorkspaceService? dashboardWorkspace = null,
    DashboardActivationService? dashboardActivation = null,
    Func<IDashboardWorkspaceHost, DashboardActivationService>? activationFactory = null) : IWorkspaceServicesFactory
{
    public WorkspaceServices Create(ITabWorkspaceHost tabHost, IDashboardWorkspaceHost dashboardHost)
    {
        var resolvedCatalog = catalog ?? new LogFileCatalogService(fileRepository);
        TabWorkspaceService? resolvedTabs = tabWorkspace;
        DashboardActivationService? resolvedActivation = dashboardActivation;
        DashboardWorkspaceService? resolvedDashboards = dashboardWorkspace;
        try
        {
            resolvedTabs ??= new TabWorkspaceService(tabHost, fileRepository, logReader, tailService, encodingDetection, resolvedCatalog, dispatcher);
            resolvedActivation ??= activationFactory?.Invoke(dashboardHost)
                ?? new DashboardActivationService(dashboardHost, fileRepository, groupRepository);
            resolvedDashboards ??= new DashboardWorkspaceService(dashboardHost, fileRepository, groupRepository, resolvedCatalog, null, resolvedActivation);
            return new WorkspaceServices(resolvedCatalog, resolvedTabs, resolvedActivation, resolvedDashboards,
                tabWorkspace == null, dashboardActivation == null, dashboardWorkspace == null);
        }
        catch (Exception constructionError)
        {
            // Supplied substitutions transfer only after the shell is fully constructed.
            var errors = new List<Exception>();
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); }
            }
            if (dashboardWorkspace == null) Cleanup(() => resolvedDashboards?.DetachGroupViewModels());
            if (dashboardActivation == null) Cleanup(() => resolvedActivation?.ShutdownMembershipRefresh());
            if (tabWorkspace == null) Cleanup(() => resolvedTabs?.Dispose());
            if (errors.Count > 0)
                throw new AggregateException("Workspace construction and cleanup failed.", new[] { constructionError }.Concat(errors));
            throw;
        }
    }
}

/// <summary>One shell's shared graph, initialization admission and owned workspace lifetime.</summary>
internal sealed class WorkspaceServices : IDisposable
{
    private readonly bool _createdTabs;
    private readonly bool _createdActivation;
    private readonly bool _createdDashboards;
    private readonly object _gate = new();
    private readonly HashSet<Task> _initializations = new();
    private Task _shutdownDrain = Task.CompletedTask;
    private Task _bindingCleanup = Task.CompletedTask;
    private bool _shutdownStarted;
    private int _disposed;

    internal WorkspaceServices(LogFileCatalogService catalog, TabWorkspaceService tabs,
        DashboardActivationService activation, DashboardWorkspaceService dashboards,
        bool createdTabs, bool createdActivation, bool createdDashboards)
    {
        Catalog = catalog;
        Tabs = tabs;
        Activation = activation;
        Dashboards = dashboards;
        _createdTabs = createdTabs;
        _createdActivation = createdActivation;
        _createdDashboards = createdDashboards;
    }

    internal LogFileCatalogService Catalog { get; }
    internal TabWorkspaceService Tabs { get; }
    internal DashboardActivationService Activation { get; }
    internal DashboardWorkspaceService Dashboards { get; }

    internal Task ShutdownTask
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_initializations.Concat([_shutdownDrain, Tabs.ShutdownTask, _bindingCleanup]));
        }
    }

    internal Task InitializeAsync(Func<Task> initialize)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_shutdownStarted) return Task.CompletedTask;
            _initializations.Add(completion.Task); // Publish ownership before initialization can call out.
        }
        _ = CompleteInitializationAsync(initialize, completion);
        return completion.Task;
    }

    private async Task CompleteInitializationAsync(Func<Task> initialize, TaskCompletionSource completion)
    {
        try
        {
            await initialize();
            completion.TrySetResult();
            lock (_gate) _initializations.Remove(completion.Task);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            _ = completion.Task.Exception; // Retain the original fault for shutdown as well as the caller.
        }
    }

    internal void BeginShutdown()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdownStarted) return;
            _shutdownStarted = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownDrain = completion.Task; // Register before cancellation can call back into the shell.
        }
        Observe(completion.Task);
        _ = CompleteShutdownAsync(completion);
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        var drains = new List<Task>();
        try { drains.Add(Activation.CancelAndDrainInteractiveLoadsAsync()); }
        catch (Exception error) { drains.Add(Task.FromException(error)); }
        try { Activation.ShutdownMembershipRefresh(); }
        catch (Exception error) { drains.Add(Task.FromException(error)); }
        drains.Add(Activation.MembershipShutdownTask);
        var drain = Task.WhenAll(drains);
        try
        {
            await drain;
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(drain.Exception?.InnerExceptions ?? (IEnumerable<Exception>)[error]);
        }
    }

    internal void AbortConstruction()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var errors = new List<Exception>();
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (_createdActivation) Cleanup(Activation.ShutdownMembershipRefresh);
        if (_createdDashboards) Cleanup(Dashboards.DetachGroupViewModels);
        if (_createdTabs) Cleanup(Tabs.Dispose);
        if (errors.Count > 0) throw new AggregateException("Workspace construction cleanup failed.", errors);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _bindingCleanup = completion.Task;
        Observe(completion.Task);
        _ = CompleteDisposalAsync(completion);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        var drains = new List<Task>();
        BeginShutdown();
        try { Tabs.Dispose(); }
        catch (Exception error) { drains.Add(Task.FromException(error)); }
        try { Dashboards.DetachGroupViewModels(); }
        catch (Exception error) { drains.Add(Task.FromException(error)); }
        lock (_gate) drains.AddRange(_initializations.Concat([_shutdownDrain, Tabs.ShutdownTask]));
        var drain = Task.WhenAll(drains);
        IEnumerable<Exception>? errors = null;
        try
        {
            await drain;
        }
        catch (Exception error)
        {
            errors = drain.Exception?.InnerExceptions ?? (IEnumerable<Exception>)[error];
        }
        finally
        {
            Activation.QueueMemberRefresh = null;
            Activation.MembershipDispatcher = null;
        }
        if (errors == null) completion.TrySetResult();
        else completion.TrySetException(errors);
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
