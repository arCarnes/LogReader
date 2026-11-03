namespace LogReader.Core.Tests.UncPerformance;

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

internal sealed record FixtureFile(string Label, string Path, long Bytes, long WriteTicks,
    FileGenerationToken Identity, string Sha256, FileEncoding Encoding);

internal sealed record FixtureManifest(int SchemaVersion, string Recipe, List<FixtureFile> Files,
    string RemoteStatus, int RemoteDiscoveredFiles, long RemoteDiscoveredBytes);

internal static class FixtureStore
{
    internal const string AllowedUncRoot = @"\\mint-serv\Plex Media\Test";
    internal const long MaximumInvocationBytes = 256L * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    internal static string RequireDescendant(string root, string path)
    {
        var normalizedRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var normalizedPath = System.IO.Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture path escaped its authorized root.");
        return normalizedPath;
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Reparse points are not fixture inputs.");
    }

    internal static string RequireLocalPath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Measurement inputs and artifacts must be local.");
        for (var ancestor = fullPath; ancestor != null; ancestor = System.IO.Path.GetDirectoryName(ancestor))
            if (File.Exists(ancestor) || Directory.Exists(ancestor)) RejectReparsePoint(ancestor);
        return fullPath;
    }

    internal static void ValidateRemotePath(string path)
    {
        var fullPath = RequireDescendant(AllowedUncRoot, path);
        // Inspect only the allowed root and descendants. Never resolve a link target.
        RejectReparsePoint(AllowedUncRoot);
        var current = AllowedUncRoot;
        foreach (var segment in System.IO.Path.GetRelativePath(AllowedUncRoot, fullPath)
                     .Split(System.IO.Path.DirectorySeparatorChar))
        {
            current = System.IO.Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    internal static List<string> DiscoverRemote(CancellationToken ct)
    {
        RejectReparsePoint(AllowedUncRoot);
        var result = new List<string>();
        var watch = Stopwatch.StartNew();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((AllowedUncRoot, 0));
        while (pending.TryDequeue(out var directory) && result.Count < 130)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("Read-only fixture discovery exceeded its cooperative time budget.");
            // No recursive framework enumeration: inspect each child before descending.
            foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("Read-only fixture discovery exceeded its cooperative time budget.");
                var child = RequireDescendant(AllowedUncRoot, entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (directory.Depth < 3)
                        pending.Enqueue((child, directory.Depth + 1));
                }
                else
                {
                    result.Add(child);
                    if (result.Count == 130)
                        break;
                }
            }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static async Task<FixtureFile> CaptureAsync(string label, string path, FileEncoding encoding,
        bool hashContent, CancellationToken ct)
    {
        path = System.IO.Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            ValidateRemotePath(path);
        else
            RejectReparsePoint(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var identity = FileGenerationTokenProvider.Capture(stream);
        if (!identity.IsKnown)
            throw new IOException("Fixture identity could not be established.");
        var bytes = stream.Length;
        var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        var hash = hashContent && bytes <= MaximumInvocationBytes
            ? Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) : string.Empty;
        if (stream.Length != bytes || File.GetLastWriteTimeUtc(path).Ticks != writeTicks)
            throw new IOException("Fixture changed during evidence capture.");
        return new(label, path, bytes, writeTicks, identity, hash, encoding);
    }

    internal static async Task VerifyAsync(FixtureFile expected, bool hashContent, CancellationToken ct)
    {
        var actual = await CaptureAsync(expected.Label, expected.Path, expected.Encoding, hashContent, ct);
        if (actual.Bytes != expected.Bytes || actual.WriteTicks != expected.WriteTicks ||
            actual.Identity != expected.Identity || hashContent && actual.Sha256 != expected.Sha256)
            throw new IOException("Fixture changed; paired exact-result comparison is invalid.");
    }

    internal static async Task<FixtureManifest> PrepareAsync(string root, bool includeUnc, CancellationToken ct)
    {
        if (!System.IO.Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Generated fixtures require an absolute local artifact directory.");
        root = RequireLocalPath(root); // A local-looking path must not redirect writes through a junction/link.
        if (Directory.Exists(root))
            throw new InvalidOperationException("Use a new fixture directory; existing evidence must not be overwritten.");
        Directory.CreateDirectory(root);
        var data = System.IO.Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        var files = new List<FixtureFile>();
        async Task AddAsync(string label, string name, byte[] bytes, FileEncoding encoding)
        {
            var path = RequireDescendant(root, System.IO.Path.Combine(data, name));
            await File.WriteAllBytesAsync(path, bytes, ct);
            files.Add(await CaptureAsync(label, path, encoding, true, ct));
        }

        for (var file = 0; file < 100; file++)
        {
            var text = string.Concat(Enumerable.Range(1, 250).Select(line =>
                $"file {file:D3} line {line:D3}{(line == 125 ? " needle" : string.Empty)}\n"));
            await AddAsync($"local-{file:D3}", $"numbered-{file:D3}.log", Encoding.UTF8.GetBytes(text), FileEncoding.Utf8);
        }

        // Exactly 128 ASCII bytes per terminated line; deterministic and without a full-size allocation.
        foreach (var (label, megabytes) in new[] { ("local-large", 128), ("local-multi-quantum", 9) })
        {
            var path = System.IO.Path.Combine(data, label + ".log");
            var block = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('x', 127) + "\n", 8192)));
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                for (var i = 0; i < megabytes; i++)
                    await stream.WriteAsync(block, ct);
            files.Add(await CaptureAsync(label, path, FileEncoding.Utf8, true, ct));
        }
        const string shortText = "one café\r\nneedle two\r\nunfinished needle";
        foreach (var (name, encoding, kind) in new[]
                 {
                     ("utf8", new UTF8Encoding(false) as Encoding, FileEncoding.Utf8),
                     ("utf8-bom", new UTF8Encoding(true), FileEncoding.Utf8Bom),
                     ("utf16", Encoding.Unicode, FileEncoding.Utf16),
                     ("utf16-bomless", new UnicodeEncoding(false, false), FileEncoding.Utf16),
                     ("utf16-be", Encoding.BigEndianUnicode, FileEncoding.Utf16Be),
                     ("ansi", Encoding.Latin1, FileEncoding.Ansi)
                 })
            await AddAsync("local-" + name, name + ".log",
                [.. encoding.GetPreamble(), .. encoding.GetBytes(shortText)], kind);
        await AddAsync("local-split-utf8", "split-utf8.log", [.. Encoding.UTF8.GetBytes("needle café"), 0xE2, 0x82], FileEncoding.Utf8);
        await AddAsync("local-split-utf16", "split-utf16.log", [.. Encoding.Unicode.GetBytes("needle"), 0xAC], FileEncoding.Utf16);
        await AddAsync("local-split-crlf", "split-crlf.log", Encoding.UTF8.GetBytes("needle\r"), FileEncoding.Utf8);

        var remoteStatus = "not-requested";
        var discovered = 0;
        long remoteBytes = 0;
        if (includeUnc)
        {
            // Store paths only in the private local manifest, never in normalized reports/stdout.
            try
            {
                var paths = DiscoverRemote(ct);
                discovered = paths.Count;
                var detector = new FileEncodingDetectionService();
                for (var i = 0; i < paths.Count; i++)
                {
                    ValidateRemotePath(paths[i]);
                    var encoding = detector.ResolveEncodingDecision(paths[i], FileEncoding.Auto).ResolvedEncoding;
                    var size = new FileInfo(paths[i]).Length;
                    var fixture = await CaptureAsync($"unc-{i:D3}", paths[i], encoding,
                        remoteBytes + size <= MaximumInvocationBytes, ct);
                    remoteBytes += fixture.Bytes;
                    files.Add(fixture);
                }
                remoteStatus = "available";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                files.RemoveAll(file => file.Label.StartsWith("unc-", StringComparison.Ordinal));
                remoteStatus = "unavailable-" + ex.GetType().Name;
            }
        }
        var manifest = new FixtureManifest(1, "numbered-v1-no-randomness", files, remoteStatus, discovered, remoteBytes);
        await File.WriteAllTextAsync(System.IO.Path.Combine(root, "manifest.private.json"),
            JsonSerializer.Serialize(manifest, Json), ct);
        return manifest;
    }
}
