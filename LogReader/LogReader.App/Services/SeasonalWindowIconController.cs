namespace LogReader.App.Services;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal sealed class SeasonalWindowIconController : IDisposable
{
    private readonly Application _application;
    private readonly Func<DateOnly> _localDateProvider;
    private readonly IReadOnlyList<SeasonalIconDefinition> _catalog;
    private readonly Func<string, ImageSource> _loadIcon;
    private readonly Action<string>? _refreshShortcut;
    private readonly ImageSource _standardIcon;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;
    private bool _started;
    private bool _disposed;

    internal SeasonalWindowIconController(
        Application application,
        Func<DateOnly>? localDateProvider = null,
        IReadOnlyList<SeasonalIconDefinition>? catalog = null,
        Func<string, ImageSource>? loadIcon = null,
        Action<string>? refreshShortcut = null)
    {
        application.Dispatcher.VerifyAccess();
        _application = application;
        _localDateProvider = localDateProvider ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _catalog = catalog ?? SeasonalIcons.Catalog;
        _loadIcon = loadIcon ?? LoadIcon;
        _refreshShortcut = refreshShortcut;
        _standardIcon = (ImageSource)application.Resources[SeasonalIcons.ResourceKey];
        _icons.Add(SeasonalIcons.StandardIcon, _standardIcon);
        _timer = new DispatcherTimer(DispatcherPriority.Background, application.Dispatcher)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
    }

    internal bool IsRunning => _timer.IsEnabled;

    internal void Start()
    {
        _application.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;

        Refresh();
        _application.Activated += OnActivated;
        _timer.Tick += OnTick;
        _timer.Start();
        _started = true;
    }

    internal void Refresh()
    {
        _application.Dispatcher.VerifyAccess();
        // A refresh queued before shutdown must not change application resources.
        if (_disposed)
            return;

        var selected = SeasonalIcons.Resolve(_localDateProvider(), _catalog);
        var seasonal = selected == null ? null : GetIcon(selected);
        var icon = seasonal ?? _standardIcon;
        if (!ReferenceEquals(_application.Resources[SeasonalIcons.ResourceKey], icon))
            _application.Resources[SeasonalIcons.ResourceKey] = icon;
        _refreshShortcut?.Invoke(seasonal == null ? SeasonalIcons.StandardIcon : selected!.IconResource);
    }

    private ImageSource? GetIcon(SeasonalIconDefinition selected)
    {
        if (_icons.TryGetValue(selected.IconResource, out var cached))
            return cached;

        ImageSource? icon = null;
        try
        {
            icon = _loadIcon(selected.IconResource);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or ArgumentException)
        {
            Trace.TraceWarning("Could not load seasonal window icon '{0}': {1}", selected.Id, ex);
        }

        // Cache failures too: do not retry/log a broken bundled resource every minute.
        _icons.Add(selected.IconResource, icon);
        return icon;
    }

    private static ImageSource LoadIcon(string resource)
    {
        var image = BitmapFrame.Create(new Uri(resource, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    private void OnActivated(object? sender, EventArgs e) => Refresh();

    private void OnTick(object? sender, EventArgs e) => Refresh();

    public void Dispose()
    {
        _application.Dispatcher.VerifyAccess();
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _application.Activated -= OnActivated;
    }
}
