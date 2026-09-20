namespace LogReader.Core.Tests;

using System.Text;
using System.Text.Json;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

public sealed class ViewBundleReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeezTailBundle-" + Guid.NewGuid().ToString("N"));
    private static byte[] Manifest(string path = "views/app.json") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, name = "Operations", views = new[] { new { id = "app", name = "Application", path } }
    });
    private static byte[] View(string path = @"C:\logs\app.log") => JsonSerializer.SerializeToUtf8Bytes(new ViewExport
    {
        Groups = [new() { Id = "app", Name = "Application", FilePaths = [path] }]
    }, JsonStore.GetOptions());

    [Fact]
    public async Task DocumentedTwoViewExample_Validates()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LogReader.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var snapshot = await new ViewBundleReader().ReadAsync(new ViewSourceRegistration
        {
            Location = Path.Combine(directory.FullName, "docs", "examples", "shared-views")
        });
        Assert.Equal(new[] { "production", "staging" }, snapshot.Views.Select(v => v.Id));
        Assert.All(snapshot.Views, view => Assert.Equal(2, view.Definition.Groups.Count));
    }

    [Fact]
    public async Task FolderSnapshot_IsStableAndChangesOnlyWhenContentsChange()
    {
        Directory.CreateDirectory(Path.Combine(_root, "views"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "weeztail.json"), Manifest());
        var file = Path.Combine(_root, "views", "app.json");
        await File.WriteAllBytesAsync(file, View());
        var source = new ViewSourceRegistration { Location = _root };
        var reader = new ViewBundleReader();
        var first = await reader.ReadAsync(source);
        var second = await reader.ReadAsync(source);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal("app", Assert.Single(second.Views).Id);
        await File.WriteAllBytesAsync(file, View(@"\\server\logs\new.log"));
        Assert.NotEqual(first.Revision, (await reader.ReadAsync(source)).Revision);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("views/../../outside.json")]
    [InlineData("C:/outside.json")]
    [InlineData("/absolute.json")]
    [InlineData("views/a.json:stream")]
    [InlineData("views/./a.json")]
    public async Task EscapingPaths_AreRejectedBeforeReadingView(string path)
    {
        var reads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ViewBundleReader.ReadBundleAsync((_, _, _) =>
        {
            reads++;
            return Task.FromResult(Manifest(path));
        }));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("relative.log")]
    [InlineData("https://host/log")]
    [InlineData(@"\\?\C:\file.log")]
    [InlineData(@"\\.\pipe\secret")]
    [InlineData(@"C:\logs\*.log")]
    [InlineData(@"C:\logs\NUL.log")]
    [InlineData(@"C:\logs\COM1")]
    public async Task UnsupportedSharedLogPaths_AreRejected(string path)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ViewBundleReader.ReadBundleAsync((name, _, _) =>
            Task.FromResult(name == "weeztail.json" ? Manifest() : View(path))));
    }

    [Fact]
    public async Task MalformedDefinition_RejectsWholeBundle()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ViewBundleReader.ReadBundleAsync((name, _, _) =>
            Task.FromResult(name == "weeztail.json" ? Manifest() : Encoding.UTF8.GetBytes("{bad"))));
    }

    [Fact]
    public async Task OversizedManifest_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ViewBundleReader.ReadBundleAsync((_, _, _) =>
            Task.FromResult(new byte[ViewBundleReader.ManifestLimit + 1])));
    }

    [Fact]
    public async Task Cancellation_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ViewBundleReader.ReadBundleAsync((_, _, _) => Task.FromResult(Manifest()), cancellation.Token));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
