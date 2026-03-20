namespace LogReader.Core;

public static class AppPaths
{
    public const string InstallConfigFileName = "LogReader.install.json";
    public const string MsiUserStorageSelectionFileName = "LogReader.msi-user.json";

    private static readonly AsyncLocal<string?> TestRootPath = new();
    private static readonly AsyncLocal<string?> TestBaseDirectory = new();
    private static readonly AsyncLocal<string?> TestMsiUserStorageSelectionPath = new();
    private static readonly AsyncLocal<bool?> TestAllowDebugFallback = new();

    public static string RootDirectory => TestRootPath.Value ?? ResolveRootDirectory();

    public static string DataDirectory => Path.Combine(RootDirectory, "Data");

    public static string ViewsDirectory => Path.Combine(DataDirectory, "Views");

    public static string CacheDirectory => Path.Combine(RootDirectory, "Cache");

    public static string IndexDirectory => Path.Combine(CacheDirectory, "idx");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static void SetRootPathForTests(string? path) => TestRootPath.Value = path;

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

    internal static string GetInstallConfigPath() => Path.Combine(GetBaseDirectory(), InstallConfigFileName);

    internal static string GetMsiUserStorageSelectionPath()
        => TestMsiUserStorageSelectionPath.Value ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogReaderSetup",
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

    internal static string GetDefaultStorageRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogReader");

    private static bool ShouldAllowDebugFallback()
    {
#if DEBUG
        return TestAllowDebugFallback.Value ?? true;
#else
        return TestAllowDebugFallback.Value ?? false;
#endif
    }
}
