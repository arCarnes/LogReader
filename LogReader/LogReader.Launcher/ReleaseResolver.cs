namespace LogReader.Launcher;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record ReleaseTarget(string DirectoryPath, string ExecutablePath)
{
    public ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = DirectoryPath
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}

public static class ReleaseResolver
{
    public static ReleaseTarget Resolve(string launcherDirectory, bool mcp = false)
    {
        var root = Path.GetFullPath(launcherDirectory);
        var manifestPath = Path.Combine(root, "current.json");
        RequireUnredirected(manifestPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.ValueKind != JsonValueKind.Object ||
            !manifest.RootElement.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Release manifest '{manifestPath}' requires a version string.");

        var version = versionElement.GetString()!;
        if (!Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant) ||
            !Version.TryParse(version, out _))
            throw new InvalidDataException($"Release manifest '{manifestPath}' requires three numeric version fields.");

        var releasesPath = Path.Combine(root, "releases");
        var releasePath = Path.Combine(releasesPath, version);
        RequireUnredirected(releasesPath);
        RequireUnredirected(releasePath);
        if (!Directory.Exists(releasePath))
            throw new DirectoryNotFoundException($"Release directory '{releasePath}' was not found.");

        var configPath = Path.Combine(releasePath, "WeezTail.install.json");
        RequireUnredirected(configPath);
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("installMode", out var installMode) ||
            installMode.ValueKind != JsonValueKind.String || installMode.GetString() != "Portable" ||
            !config.RootElement.TryGetProperty("storageMode", out var storageMode) ||
            storageMode.ValueKind != JsonValueKind.String || storageMode.GetString() != "LocalAppData")
            throw new InvalidDataException($"Release configuration '{configPath}' must use Portable/LocalAppData storage.");

        var executablePath = Path.Combine(releasePath, mcp ? "WeezTail.Mcp.exe" : "WeezTail.exe");
        RequireUnredirected(executablePath);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"Release executable '{executablePath}' was not found.", executablePath);
        return new ReleaseTarget(releasePath, executablePath);
    }

    private static void RequireUnredirected(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Release path '{path}' must not be redirected.");
    }
}
