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

        if (IsUnsafeBroadRoot(normalizedRoot))
        {
            throw new StorageValidationException(
                normalizedRoot,
                $"Choose a LogReader-specific storage folder instead of a broad system or profile folder:{Environment.NewLine}{normalizedRoot}");
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

    internal static bool IsUnsafeBroadRoot(string path)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(NormalizePath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalizedPath) ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var broadRoot in GetBroadProfileRoots())
        {
            var normalizedBroadRoot = Path.TrimEndingDirectorySeparator(NormalizePath(broadRoot));
            if (!IsSamePathOrDescendant(normalizedPath, normalizedBroadRoot))
                continue;

            return !HasLogReaderSpecificSegment(normalizedPath, normalizedBroadRoot);
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

    private static IEnumerable<string> GetBroadProfileRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath()
        };

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new StorageValidationException(
                path,
                $"The storage location is not a valid path:{Environment.NewLine}{path}");
        }

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

    private static bool IsSamePathOrDescendant(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLogReaderSpecificSegment(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return false;

        var relativePath = Path.GetRelativePath(root, path);
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Contains(AppPaths.DefaultStorageRootDirectoryName, StringComparison.OrdinalIgnoreCase));
    }
}
