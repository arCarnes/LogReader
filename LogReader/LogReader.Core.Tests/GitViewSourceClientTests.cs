namespace LogReader.Core.Tests;

using System.Text;
using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

public sealed class GitViewSourceClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeezTailGit-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope;
    private readonly GitProcessRunner _runner = new();
    private string Remote => Path.Combine(_root, "remote");
    public GitViewSourceClientTests()
    {
        _scope = AppPaths.BeginTestScope(rootPath: _root);
        Directory.CreateDirectory(Remote);
    }

    [Fact]
    public async Task BranchRefresh_TagAndCommitPins_AndCacheRepair_WorkWithRealGit()
    {
        await Git("init", "--initial-branch=main");
        await WriteView("First");
        await Git("add", ".");
        await Git("-c", "user.name=View Tests", "-c", "user.email=views@example.invalid", "commit", "-m", "first");
        await Git("tag", "stable");
        var client = new GitViewSourceClient(_runner, true);
        var source = new ViewSourceRegistration { Kind = ViewSourceKind.Git, Location = Remote };
        var refs = await client.GetReferencesAsync(source);
        Assert.Equal("main", refs.DefaultBranch);
        Assert.Contains("main", refs.Branches);
        Assert.Contains("stable", refs.Tags);
        var first = await client.ReadAsync(source);
        Assert.Equal("main", source.Reference);
        Assert.Equal("First", first.Views[0].Definition.Groups[0].Name);
        source.RevisionKind = ViewRevisionKind.Tag;
        source.Reference = "stable";
        source.Commit = first.Commit;
        await WriteView("Second");
        await Git("add", ".");
        await Git("-c", "user.name=View Tests", "-c", "user.email=views@example.invalid", "commit", "-m", "second");
        await Git("tag", "-f", "stable");
        Assert.Equal(first.Commit, (await client.ReadAsync(source)).Commit);
        source.Commit = null;
        var second = await client.ReadAsync(source);
        Assert.NotEqual(first.Commit, second.Commit);
        source.RevisionKind = ViewRevisionKind.Commit;
        source.Reference = first.Commit!;
        Assert.Equal(first.Commit, (await client.ReadAsync(source)).Commit);
        source.RevisionKind = ViewRevisionKind.Branch;
        source.Reference = "main";
        source.Commit = first.Commit;
        Assert.Equal(second.Commit, (await client.ReadAsync(source)).Commit);
        var cache = Path.Combine(AppPaths.CacheDirectory, "view-sources", source.Id);
        await File.WriteAllTextAsync(Path.Combine(cache, "HEAD"), "broken");
        Assert.Equal(second.Commit, (await client.ReadAsync(source)).Commit);
        await client.RemoveCacheAsync(source.Id);
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(Path.Combine(Remote, ".git")));
    }

    [Fact]
    public async Task RepositorySymlink_IsRejectedWithoutCheckout()
    {
        await Git("init", "--initial-branch=main");
        await WriteView("Initial");
        await Git("add", ".");
        await File.WriteAllTextAsync(Path.Combine(Remote, "link-target.txt"), "../outside.json");
        var blob = await Git("hash-object", "-w", "link-target.txt");
        await Git("update-index", "--add", "--cacheinfo", "120000," + blob + ",app.json");
        await Git("-c", "user.name=View Tests", "-c", "user.email=views@example.invalid", "commit", "-m", "symlink bundle");
        var client = new GitViewSourceClient(_runner, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(new() { Location = Remote, Kind = ViewSourceKind.Git, Reference = "main" }));
        Assert.False(File.Exists(Path.Combine(_root, "outside.json")));
    }

    [Fact]
    public async Task MissingRefAndInvalidBundle_DoNotChangeTheRemote()
    {
        await Git("init", "--initial-branch=main");
        await WriteView("Initial");
        await Git("add", ".");
        await Git("-c", "user.name=View Tests", "-c", "user.email=views@example.invalid", "commit", "-m", "initial");
        var head = await Git("rev-parse", "HEAD");
        var client = new GitViewSourceClient(_runner, true);
        var source = new ViewSourceRegistration { Kind = ViewSourceKind.Git, Location = Remote, Reference = "missing" };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(source));
        Assert.Equal(head, await Git("rev-parse", "HEAD"));
        await File.WriteAllTextAsync(Path.Combine(Remote, "weeztail.json"), "invalid");
        await Git("add", ".");
        await Git("-c", "user.name=View Tests", "-c", "user.email=views@example.invalid", "commit", "-m", "invalid bundle");
        source.Reference = "main";
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(source));
    }

    [Theory]
    [InlineData("https://user:secret@example.com/views.git")]
    [InlineData("https://example.com/views.git?token=secret")]
    [InlineData("http://example.com/views.git")]
    [InlineData("ftp://example.com/views.git")]
    [InlineData("file:///C:/repository")]
    [InlineData("ext::dangerous-command")]
    [InlineData("--upload-pack=command")]
    public void UnsafeRemote_IsRejected(string remote) => Assert.Throws<InvalidDataException>(() => GitViewSourceClient.ValidateRemote(remote));

    [Theory]
    [InlineData("https://github.com/team/views.git")]
    [InlineData("git@github.com:team/views.git")]
    [InlineData("ssh://git@example.com/team/views.git")]
    public void SupportedRemote_IsAccepted(string remote) => GitViewSourceClient.ValidateRemote(remote);

    [Fact]
    public async Task MissingGit_ReportsActionableError()
    {
        var runner = new GitProcessRunner(Path.Combine(_root, "missing-git.exe"));
        var error = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Remote, ["--version"], 1024, default));
        Assert.Contains("Install Git for Windows", error.Message);
    }

    [Fact]
    public async Task ProcessOutput_IsBounded_AndCancellationIsObserved()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _runner.RunAsync(Remote, ["--version"], 1, default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new GitViewSourceClient(_runner, true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetReferencesAsync(new() { Location = Remote }, cts.Token));
    }

    [Fact]
    public async Task FailedProcess_DoesNotExposeCredentialHelperOutput()
    {
        var client = new GitViewSourceClient(new ErrorRunner(), false);
        var error = await Assert.ThrowsAsync<IOException>(() => client.GetReferencesAsync(new() { Location = "https://example.com/views.git" }));
        Assert.DoesNotContain("secret-token", error.ToString());
    }

    private async Task<string> Git(params string[] args)
    {
        var result = await _runner.RunAsync(Remote, args, 16384, default);
        Assert.True(result.ExitCode == 0, result.Error);
        return Encoding.UTF8.GetString(result.Output).Trim();
    }

    private async Task WriteView(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(Remote, "weeztail.json"), """
            { "schemaVersion": 1, "name": "Team", "views": [{ "id": "app", "name": "Application", "path": "app.json" }] }
            """);
        await File.WriteAllTextAsync(Path.Combine(Remote, "app.json"), JsonSerializer.Serialize(new ViewExport
        {
            Groups = [new() { Id = "app", Name = name }]
        }, JsonStore.GetOptions()));
    }

    private sealed class ErrorRunner : IGitProcessRunner
    {
        public Task<GitProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments, int limit, CancellationToken ct)
        {
            Assert.Contains("protocol.allow=never", arguments);
            Assert.DoesNotContain("push", arguments);
            return Task.FromResult(new GitProcessResult(128, [], "secret-token"));
        }
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
    }
}
