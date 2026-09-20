namespace LogReader.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class ViewBundleReader : IViewSourceReader
{
    public const int ManifestLimit = 1024 * 1024;
    public const int ViewLimit = 16 * 1024 * 1024;
    public const int BundleLimit = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonStore.GetOptions()) { PropertyNameCaseInsensitive = true, MaxDepth = 64 };

    public async Task<ViewSourceSnapshot> ReadAsync(ViewSourceRegistration source, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(source.Location);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var first = await ReadBundleAsync(Read, cancellationToken).ConfigureAwait(false);
            var second = await ReadBundleAsync(Read, cancellationToken).ConfigureAwait(false);
            if (first.Revision == second.Revision) return second;
        }
        throw new IOException("The source changed while it was being read. Wait for the files to finish updating and refresh again.");

        Task<byte[]> Read(string relative, int limit, CancellationToken ct)
        {
            var path = Path.GetFullPath(Path.Combine(root, NormalizePath(relative).Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A view definition escapes its source folder.");
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("View bundles cannot contain symbolic links or reparse points.");
            return ReadBoundedAsync(path, limit, ct);
        }
    }

    public static async Task<ViewSourceSnapshot> ReadBundleAsync(
        Func<string, int, CancellationToken, Task<byte[]>> read, CancellationToken cancellationToken = default)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var manifestBytes = await Read("weeztail.json", ManifestLimit);
            var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestBytes, Options)
                ?? throw new InvalidDataException("The view manifest is empty.");
            if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Views == null || manifest.Views.Count == 0)
                throw new InvalidDataException("weeztail.json must contain schemaVersion 1, a name, and at least one view.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "weeztail.json" };
            var result = new ViewSourceSnapshot { Name = manifest.Name };
            foreach (var entry in manifest.Views)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id) || string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException("Manifest views require unique nonblank IDs and names.");
                var path = NormalizePath(entry.Path);
                if (!paths.Add(path)) throw new InvalidDataException("Manifest paths must be unique.");
                var definition = JsonSerializer.Deserialize<ViewExport>(await Read(path, ViewLimit), Options)
                    ?? throw new InvalidDataException($"View '{entry.Name}' is empty.");
                DashboardTopologyValidator.ValidateImportedView(definition);
                foreach (var file in definition.Groups.SelectMany(g => g.FilePaths))
                    if (!IsSharedLogPath(file))
                        throw new InvalidDataException($"View '{entry.Name}' contains a non-absolute or unsupported log path. Use explicit Windows drive or UNC paths.");
                result.Views.Add(new SavedView { Id = entry.Id, Name = entry.Name, Definition = definition });
            }
            result.Revision = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return result;

            async Task<byte[]> Read(string path, int limit)
            {
                var bytes = await read(path, limit, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > limit || (total += bytes.Length) > BundleLimit)
                    throw new InvalidDataException("The view bundle exceeds its size limit.");
                hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
                return bytes;
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid view bundle JSON: " + ex.Message, ex); }
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("A manifest view path is required.");
        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            IsDeviceName(part) || part.Any(c => c < 32 || ":*?\"<>|".Contains(c))))
            throw new InvalidDataException("Manifest paths must be ordinary relative file paths within the source.");
        return path;
    }

    internal static bool IsSharedLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normal = path.Replace('/', '\\');
        if (normal.Split('\\').Any(IsDeviceName)) return false;
        if (normal.StartsWith(@"\\?\") || normal.StartsWith(@"\\.\") || normal.Any(c => c < 32 || "*?\"<>|".Contains(c))) return false;
        if (normal.Length > 3 && char.IsAsciiLetter(normal[0]) && normal[1] == ':' && normal[2] == '\\')
            return !normal[2..].Contains(':');
        return normal.StartsWith(@"\\") && !normal.Contains(':') && normal[2..].Split('\\').Length >= 3 &&
            normal[2..].Split('\\').All(p => p.Length > 0 && p is not "." and not "..");
    }

    private static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9';
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length > limit) throw new InvalidDataException("A view bundle file exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private sealed class BundleManifest
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<BundleView>? Views { get; set; }
    }
    private sealed class BundleView
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }
}
