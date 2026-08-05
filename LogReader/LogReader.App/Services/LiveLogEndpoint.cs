namespace LogReader.App.Services;

using System.Diagnostics;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

internal interface IAppLiveLogEndpoint : IDisposable
{
    bool TryStart();

    void BeginStop();
}

/// <summary>
/// Owns the optional UI-process log endpoint while borrowing the tab workspace's session registry.
/// Construction and idle listening do not read the catalog or open configured logs.
/// </summary>
internal sealed class LiveLogEndpoint : IAppLiveLogEndpoint
{
    private readonly ISearchService _searchService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly IBoundedLogReaderService _logReader;
    private readonly FileSessionRegistry _registry;
    private readonly Func<IConfiguredLogCatalogReader> _catalogFactory;
    private readonly Func<ILogQueryBackend, LiveLogIpcServer> _serverFactory;
    private readonly Action<string> _diagnostic;
    private readonly object _lifecycleGate = new();
    private IConfiguredLogCatalogReader? _catalog;
    private ConfiguredLogQueryBackend? _backend;
    private LiveLogIpcServer? _server;
    private bool _started;
    private bool _disposed;

    public LiveLogEndpoint(
        IBoundedLogReaderService logReader,
        ISearchService searchService,
        IEncodingDetectionService encodingDetectionService,
        FileSessionRegistry registry)
        : this(
            logReader,
            searchService,
            encodingDetectionService,
            registry,
            static () => new PersistedDashboardSnapshotReader(),
            backend => new LiveLogIpcServer(
                LiveLogPipeIdentityFactory.CreateCurrent(AppPaths.RootDirectory),
                backend,
                static code => Debug.WriteLine(code)),
            static code => Debug.WriteLine(code))
    {
    }

    internal LiveLogEndpoint(
        IBoundedLogReaderService logReader,
        ISearchService searchService,
        IEncodingDetectionService encodingDetectionService,
        FileSessionRegistry registry,
        Func<IConfiguredLogCatalogReader> catalogFactory,
        Func<ILogQueryBackend, LiveLogIpcServer> serverFactory,
        Action<string>? diagnostic = null)
    {
        _logReader = logReader ?? throw new ArgumentNullException(nameof(logReader));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _encodingDetectionService = encodingDetectionService ?? throw new ArgumentNullException(nameof(encodingDetectionService));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _catalogFactory = catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));
        _serverFactory = serverFactory ?? throw new ArgumentNullException(nameof(serverFactory));
        _diagnostic = diagnostic ?? (_ => { });
    }

    public bool TryStart()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return false;
            if (_started)
                return true;

            IConfiguredLogCatalogReader? catalog = null;
            ConfiguredLogQueryBackend? backend = null;
            LiveLogIpcServer? server = null;
            try
            {
                catalog = _catalogFactory();
                var sessionProvider = new UiIndexedLogSessionProvider(_registry);
                var limits = LogQueryEffectiveLimits.Default with
                {
                    MaximumConcurrentDiskOperations = 1,
                    MaximumIndexedSessions = UiIndexedLogSessionProvider.DefaultMaximumAgentSessions,
                    MaximumMappedLineOffsets = UiIndexedLogSessionProvider.DefaultMaximumAgentMappedLineOffsets,
                    IndexedSessionWarmRetentionMilliseconds = 0
                };
                backend = new ConfiguredLogQueryBackend(
                    catalog,
                    _searchService,
                    _encodingDetectionService,
                    _logReader,
                    sessionProvider,
                    LogOperationBackendKind.LiveUi,
                    "ui_shared",
                    limits);
                server = _serverFactory(backend);
                if (!server.TryStart())
                {
                    DisposeResources(server, backend, catalog);
                    return false;
                }

                _catalog = catalog;
                _backend = backend;
                _server = server;
                _started = true;
                return true;
            }
            catch (Exception)
            {
                DisposeResources(server, backend, catalog);
                _diagnostic("live_log_endpoint_start_failed");
                return false;
            }
        }
    }

    public void BeginStop()
    {
        LiveLogIpcServer? server;
        lock (_lifecycleGate)
            server = _server;
        server?.BeginStop();
    }

    public void Dispose()
    {
        LiveLogIpcServer? server;
        ConfiguredLogQueryBackend? backend;
        IConfiguredLogCatalogReader? catalog;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _started = false;
            server = _server;
            backend = _backend;
            catalog = _catalog;
            _server = null;
            _backend = null;
            _catalog = null;
        }

        server?.BeginStop();
        DisposeResources(server, backend, catalog);
    }

    private static void DisposeResources(
        LiveLogIpcServer? server,
        ConfiguredLogQueryBackend? backend,
        IConfiguredLogCatalogReader? catalog)
    {
        server?.Dispose();
        backend?.Dispose();
        (catalog as IDisposable)?.Dispose();
    }
}
