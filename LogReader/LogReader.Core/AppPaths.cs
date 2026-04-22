namespace LogReader.Core;

public static class AppPaths
{
    public const string InstallConfigFileName = "LogReader.install.json";
    public const string MsiUserStorageSelectionFileName = "LogReader.msi-user.json";
    public const string SetupDirectoryName = "LogReaderSetup";
    public const string DefaultStorageRootDirectoryName = "LogReader";
    public const string DataFolderName = "Data";
    public const string ViewsFolderName = "Views";
    public const string CacheFolderName = "Cache";
    public const string IndexFolderName = "idx";

    private static readonly AsyncLocal<string?> TestRootPath = new();
    private static readonly AsyncLocal<string?> TestBaseDirectory = new();
    private static readonly AsyncLocal<string?> TestMsiUserStorageSelectionPath = new();
    private static readonly AsyncLocal<bool?> TestAllowDebugFallback = new();
    private static readonly AsyncLocal<string?> TestDefaultStorageRoot = new();
    private static readonly AsyncLocal<bool?> TestUseLocalAppDataDefaultStorageRoot = new();

    public static string RootDirectory => TestRootPath.Value ?? ResolveRootDirectory();

    public static string DataDirectory => Path.Combine(RootDirectory, DataFolderName);

    public static string ViewsDirectory => Path.Combine(DataDirectory, ViewsFolderName);

    public static string CacheDirectory => Path.Combine(RootDirectory, CacheFolderName);

    public static string IndexDirectory => Path.Combine(CacheDirectory, IndexFolderName);

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static void SetRootPathForTests(string? path) => TestRootPath.Value = path;

    internal static IDisposable BeginTestScope(
        string? rootPath = null,
        string? baseDirectory = null,
        string? msiUserStorageSelectionPath = null,
        bool? allowDebugFallback = true,
        string? defaultStorageRoot = null,
        bool useLocalAppDataDefaultStorageRoot = false)
    {
        var previous = new TestOverrideSnapshot(
            TestRootPath.Value,
            TestBaseDirectory.Value,
            TestMsiUserStorageSelectionPath.Value,
            TestAllowDebugFallback.Value,
            TestDefaultStorageRoot.Value,
            TestUseLocalAppDataDefaultStorageRoot.Value);

        TestRootPath.Value = rootPath;
        TestBaseDirectory.Value = baseDirectory;
        TestMsiUserStorageSelectionPath.Value = msiUserStorageSelectionPath;
        TestAllowDebugFallback.Value = allowDebugFallback;
        TestUseLocalAppDataDefaultStorageRoot.Value = useLocalAppDataDefaultStorageRoot;
        TestDefaultStorageRoot.Value = useLocalAppDataDefaultStorageRoot
            ? null
            : defaultStorageRoot ?? rootPath ?? baseDirectory;

        return new TestOverrideScope(previous);
    }

    public static void ValidateStorageRoot(string storageRootPath)
        => StoragePathValidator.ValidateStorageRoot(storageRootPath);

    internal static string ResolveRootDirectory()
        => LoadStorageConfiguration().ResolveStorageRoot(
            GetBaseDirectory(),
            GetInstallConfigPath(),
            GetMsiUserStorageSelectionPath(),
            GetDefaultStorageRoot());

    internal static AppStorageConfiguration LoadStorageConfiguration()
    {
        var configPath = GetInstallConfigPath();
        if (!File.Exists(configPath))
        {
            if (ShouldAllowDebugFallback())
            {
                return AppStorageConfiguration.CreateDebugFallback(GetDefaultStorageRoot());
            }

            throw new InstallConfigurationException(
                "The install configuration file is missing for this build.",
                configPath);
        }

        return AppStorageConfiguration.Load(configPath);
    }

    public static void ValidateStorageConfiguration()
    {
        StoragePathValidator.ValidateStorageRoot(RootDirectory);
        EnsureDirectory(DataDirectory);
        EnsureDirectory(CacheDirectory);
    }

    public static bool IsPerUserStorageSelectionConfigured()
    {
        try
        {
            return LoadStorageConfiguration().StorageMode == StorageMode.PerUserChoice;
        }
        catch (InstallConfigurationException)
        {
            return false;
        }
    }

    internal static string GetInstallConfigPath() => Path.Combine(GetBaseDirectory(), InstallConfigFileName);

    internal static string GetMsiUserStorageSelectionPath()
        => TestMsiUserStorageSelectionPath.Value ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SetupDirectoryName,
            MsiUserStorageSelectionFileName);

    public static void SaveMsiUserStorageSelection(string storageRootPath)
        => MsiUserStorageSelection.Save(GetMsiUserStorageSelectionPath(), storageRootPath);

    public static string? TryGetDataDirectoryForMessage()
    {
        try
        {
            return DataDirectory;
        }
        catch
        {
            return null;
        }
    }

    internal static void SetBaseDirectoryForTests(string? path) => TestBaseDirectory.Value = path;

    internal static void SetMsiUserStorageSelectionPathForTests(string? path)
        => TestMsiUserStorageSelectionPath.Value = path;

    internal static void SetAllowDebugFallbackForTests(bool? enabled) => TestAllowDebugFallback.Value = enabled;

    private static string GetBaseDirectory() => TestBaseDirectory.Value ?? AppContext.BaseDirectory;

    internal static string GetDefaultStorageRoot()
    {
        if (TestUseLocalAppDataDefaultStorageRoot.Value == true)
            return GetLocalAppDataDefaultStorageRoot();

        if (TestDefaultStorageRoot.Value != null)
            return TestDefaultStorageRoot.Value;

#if DEBUG
        if (TryGetSourceDebugStorageRoot(out var debugStorageRoot))
            return debugStorageRoot;
#endif

        return GetLocalAppDataDefaultStorageRoot();
    }

    private static bool ShouldAllowDebugFallback()
    {
#if DEBUG
        return TestAllowDebugFallback.Value ?? true;
#else
        return TestAllowDebugFallback.Value ?? false;
#endif
    }

    private static string GetLocalAppDataDefaultStorageRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DefaultStorageRootDirectoryName);

#if DEBUG
    private static bool TryGetSourceDebugStorageRoot(out string storageRoot)
    {
        for (var directory = new DirectoryInfo(GetBaseDirectory());
             directory != null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "LogReader.sln")))
                continue;

            storageRoot = Path.Combine(directory.FullName, ".dev-storage", DefaultStorageRootDirectoryName);
            return true;
        }

        storageRoot = string.Empty;
        return false;
    }
#endif

    private readonly record struct TestOverrideSnapshot(
        string? RootPath,
        string? BaseDirectory,
        string? MsiUserStorageSelectionPath,
        bool? AllowDebugFallback,
        string? DefaultStorageRoot,
        bool? UseLocalAppDataDefaultStorageRoot);

    private sealed class TestOverrideScope : IDisposable
    {
        private readonly TestOverrideSnapshot _previous;
        private bool _disposed;

        public TestOverrideScope(TestOverrideSnapshot previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            TestRootPath.Value = _previous.RootPath;
            TestBaseDirectory.Value = _previous.BaseDirectory;
            TestMsiUserStorageSelectionPath.Value = _previous.MsiUserStorageSelectionPath;
            TestAllowDebugFallback.Value = _previous.AllowDebugFallback;
            TestDefaultStorageRoot.Value = _previous.DefaultStorageRoot;
            TestUseLocalAppDataDefaultStorageRoot.Value = _previous.UseLocalAppDataDefaultStorageRoot;
            _disposed = true;
        }
    }
}
