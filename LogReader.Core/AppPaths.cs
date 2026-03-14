namespace LogReader.Core;

public static class AppPaths
{
    private static readonly AsyncLocal<string?> TestRootPath = new();

    public static string RootDirectory => TestRootPath.Value ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogReader");

    public static string DataDirectory => Path.Combine(RootDirectory, "Data");

    public static string CacheDirectory => Path.Combine(RootDirectory, "Cache");

    public static string IndexDirectory => Path.Combine(CacheDirectory, "idx");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static void SetRootPathForTests(string? path) => TestRootPath.Value = path;
}
