namespace LogReader.Infrastructure.Services;

using System.Text;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class GitViewSourceClient : IGitViewSourceClient
{
    private readonly IGitProcessRunner _runner;
    private readonly bool _allowFixtureTransport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public GitViewSourceClient() : this(new GitProcessRunner(), false) { }
    internal GitViewSourceClient(IGitProcessRunner runner, bool allowFixtureTransport)
    { _runner = runner; _allowFixtureTransport = allowFixtureTransport; }
    private string CacheRoot => AppPaths.EnsureDirectory(Path.Combine(AppPaths.CacheDirectory, "view-sources"));

    public async Task<GitViewReferences> GetReferencesAsync(ViewSourceRegistration source, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
            return await ReferencesAsync(source, cache, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ViewSourceSnapshot> ReadAsync(ViewSourceRegistration source, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
            var reference = source.Reference;
            if (source.RevisionKind == ViewRevisionKind.Branch && string.IsNullOrWhiteSpace(reference))
                reference = (await ReferencesAsync(source, cache, cancellationToken).ConfigureAwait(false)).DefaultBranch
                    ?? throw new InvalidOperationException("This remote has no default branch. Select a branch explicitly.");
            string revision;
            if (source.RevisionKind is ViewRevisionKind.Tag or ViewRevisionKind.Commit && source.Commit != null)
                revision = ValidateCommit(source.Commit);
            else if (source.RevisionKind == ViewRevisionKind.Commit)
                revision = ValidateCommit(reference);
            else
            {
                if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-') || reference.Any(char.IsControl))
                    throw new InvalidDataException("Select a valid Git reference.");
                var refs = await ListRefsAsync(cache, cancellationToken).ConfigureAwait(false);
                revision = source.RevisionKind == ViewRevisionKind.Branch ? "refs/remotes/origin/" + reference : "refs/tags/" + reference;
                if (!refs.Contains(revision, StringComparer.Ordinal)) throw new InvalidDataException("The selected Git reference is missing. Choose another reference.");
            }
            var commit = Text(await RunAsync(cache, ["rev-parse", "--verify", revision + "^{commit}"], 1024, cancellationToken)).Trim();
            ValidateCommit(commit);
            var snapshot = await ViewBundleReader.ReadBundleAsync(async (path, limit, ct) =>
            {
                var tree = Text(await RunAsync(cache, ["ls-tree", "-z", commit, "--", path], 4096, ct));
                var match = Regex.Match(tree, @"\A(100644|100755) blob ([a-f0-9]{40,64})\t([^\0]+)\0\z");
                if (!match.Success || match.Groups[3].Value != path)
                    throw new InvalidDataException($"'{path}' must be an ordinary file in the selected commit. Links and submodules are unsupported.");
                var objectId = match.Groups[2].Value;
                var size = Text(await RunAsync(cache, ["cat-file", "-s", objectId], 128, ct));
                if (!long.TryParse(size.Trim(), out var length) || length > limit)
                    throw new InvalidDataException("A Git view file exceeds its size limit.");
                return await RunAsync(cache, ["cat-file", "blob", objectId], limit, ct);
            }, cancellationToken).ConfigureAwait(false);
            snapshot.Commit = commit;
            source.Reference = reference;
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> FetchAsync(ViewSourceRegistration source, CancellationToken ct)
    {
        ValidateRemote(source.Location, _allowFixtureTransport);
        var cache = Path.Combine(CacheRoot, JsonViewLibraryRepository.SafeId(source.Id));
        var usable = false;
        if (Directory.Exists(cache))
        {
            EnsureOwnedDirectory(cache);
            var result = await RawAsync(cache, ["rev-parse", "--is-bare-repository"], 1024, ct);
            usable = result.ExitCode == 0 && Text(result.Output).Trim() == "true";
        }
        if (usable)
        {
            try { await FetchIntoAsync(cache, source.Location, ct); return cache; }
            catch (IOException) { /* Retry in a fresh cache; the accepted snapshot is independent. */ }
        }
        var temporary = Path.Combine(CacheRoot, source.Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await RunAsync(temporary, ["init", "--bare", "--template="], 8192, ct);
            await FetchIntoAsync(temporary, source.Location, ct);
            var backup = cache + "-old-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(cache)) Directory.Move(cache, backup);
            try { Directory.Move(temporary, cache); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, cache); throw; }
            DeleteOwnedDirectory(backup);
            return cache;
        }
        finally { DeleteOwnedDirectory(temporary); }
    }

    private async Task FetchIntoAsync(string cache, string remote, CancellationToken ct)
        => await RunAsync(cache, ["fetch", "--prune", "--force", "--no-recurse-submodules", "--no-write-fetch-head", "--", remote,
            "+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"], 8192, ct);

    private async Task<GitViewReferences> ReferencesAsync(ViewSourceRegistration source, string cache, CancellationToken ct)
    {
        var refs = await ListRefsAsync(cache, ct);
        var head = Text(await RunAsync(cache, ["ls-remote", "--symref", "--", source.Location, "HEAD"], 8192, ct));
        var match = Regex.Match(head, @"(?m)^ref: refs/heads/([^\r\n\t]+)\tHEAD\r?$");
        return new(match.Success ? match.Groups[1].Value : null,
            refs.Where(r => r.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)).Select(r => r[20..]).ToArray(),
            refs.Where(r => r.StartsWith("refs/tags/", StringComparison.Ordinal)).Select(r => r[10..]).ToArray());
    }

    private async Task<string[]> ListRefsAsync(string cache, CancellationToken ct)
        => Text(await RunAsync(cache, ["for-each-ref", "--format=%(refname)", "refs/remotes/origin/", "refs/tags/"], ViewBundleReader.ManifestLimit, ct))
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private async Task<byte[]> RunAsync(string directory, IReadOnlyList<string> arguments, int limit, CancellationToken ct)
    {
        var result = await RawAsync(directory, arguments, limit, ct);
        if (result.ExitCode != 0)
            throw new IOException($"Git {arguments[0]} failed (exit {result.ExitCode}). Check the remote, selected revision, network, and your Git credential helper or SSH agent. Accepted views were preserved.");
        return result.Output;
    }

    private Task<GitProcessResult> RawAsync(string directory, IReadOnlyList<string> arguments, int limit, CancellationToken ct)
    {
        var hooks = Path.Combine(CacheRoot, "disabled-hooks");
        Directory.CreateDirectory(hooks);
        List<string> safeArguments = ["-c", "core.hooksPath=" + hooks, "-c", "protocol.allow=never",
            "-c", "protocol.https.allow=always", "-c", "protocol.ssh.allow=always", "-c", "protocol.file.allow=never",
            "-c", "protocol.ext.allow=never", "-c", "protocol.http.allow=never", "-c", "maintenance.auto=false", "-c", "gc.auto=0"];
        if (_allowFixtureTransport) safeArguments.AddRange(["-c", "protocol.file.allow=always"]);
        safeArguments.AddRange(arguments);
        return _runner.RunAsync(directory, safeArguments, limit, ct);
    }

    internal static void ValidateRemote(string remote, bool allowFixtureTransport = false)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote.StartsWith('-') || remote.Any(char.IsControl))
            throw new InvalidDataException("Enter an HTTPS or SSH Git remote.");
        if (allowFixtureTransport && Path.IsPathFullyQualified(remote)) return;
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" &&
            !string.IsNullOrWhiteSpace(uri.Host) && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.Scheme == "ssh" ? !uri.UserInfo.Contains(':') : uri.UserInfo.Length == 0)) return;
        if (!remote.Contains("://", StringComparison.Ordinal) && Regex.IsMatch(remote, @"\A(?:[A-Za-z0-9_.-]+@)?[A-Za-z0-9][A-Za-z0-9.-]*:[^:\s]+\z") &&
            !Regex.IsMatch(remote, @"\A[A-Za-z]:")) return;
        throw new InvalidDataException("Use an HTTPS or SSH remote without embedded passwords, tokens, or query parameters. Authenticate through Git outside WeezTail.");
    }

    private static string ValidateCommit(string commit) => Regex.IsMatch(commit, @"\A[a-fA-F0-9]{40}(?:[a-fA-F0-9]{24})?\z")
        ? commit : throw new InvalidDataException("Enter a full Git commit ID available in the fetched repository.");
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public Task RemoveCacheAsync(string sourceId)
    {
        DeleteOwnedDirectory(Path.Combine(CacheRoot, JsonViewLibraryRepository.SafeId(sourceId)));
        return Task.CompletedTask;
    }

    private void EnsureOwnedDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) != Path.GetFullPath(CacheRoot) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Git cache path is not an ordinary application-owned directory.");
    }
    private void DeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        EnsureOwnedDirectory(path);
        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The Git cache contains a link; it was preserved for inspection.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                else if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        Directory.Delete(path, true);
    }
}
