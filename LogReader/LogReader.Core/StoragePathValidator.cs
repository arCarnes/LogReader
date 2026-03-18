namespace LogReader.Core;

internal static class StoragePathValidator
{
    public static void ValidateStorageRoot(
        string rootDirectory,
        Func<string, bool>? isProtectedPath = null,
        Action<string>? ensureDirectory = null,
        Func<string, string>? createProbePath = null,
        Action<string>? writeProbe = null,
        Action<string>? deleteProbe = null)
    {
        var normalizedRoot = NormalizePath(rootDirectory);
        isProtectedPath ??= IsProtectedPath;
        ensureDirectory ??= static path => _ = Directory.CreateDirectory(path);
        createProbePath ??= static root => Path.Combine(root, $".logreader-write-test-{Guid.NewGuid():N}.tmp");
        writeProbe ??= static path => File.WriteAllText(path, "LogReader write probe");
        deleteProbe ??= static path =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        };

        if (isProtectedPath(normalizedRoot))
        {
            throw new ProtectedStorageLocationException(normalizedRoot);
        }

        try
        {
            ensureDirectory(normalizedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageValidationException(
                normalizedRoot,
                $"LogReader could not create or access the storage location:{Environment.NewLine}{normalizedRoot}",
                ex);
        }

        var probePath = createProbePath(normalizedRoot);
        try
        {
            writeProbe(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageValidationException(
                normalizedRoot,
                $"LogReader could not write to the storage location:{Environment.NewLine}{normalizedRoot}",
                ex);
        }
        finally
        {
            try
            {
                deleteProbe(probePath);
            }
            catch
            {
                // Best effort cleanup for the temporary write probe.
            }
        }
    }

    internal static bool IsProtectedPath(string path)
    {
        var normalizedPath = NormalizePathWithTrailingSeparator(path);

        foreach (var protectedRoot in GetProtectedRoots())
        {
            if (normalizedPath.StartsWith(
                    NormalizePathWithTrailingSeparator(protectedRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetProtectedRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StorageValidationException(
                path,
                $"The storage location is not a valid path:{Environment.NewLine}{path}",
                ex);
        }
    }

    private static string NormalizePathWithTrailingSeparator(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(NormalizePath(path));
        return normalized + Path.DirectorySeparatorChar;
    }
}
