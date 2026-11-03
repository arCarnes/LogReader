namespace LogReader.Core.Tests;

using LogReader.Core.Tests.UncPerformance;
using LogReader.Core.Models;

public sealed class UncPerformanceFixtureTests
{
    [Theory]
    [InlineData(@"\\mint-serv\Plex Media\Test\file.log")]
    [InlineData(@"\\MINT-SERV\plex media\test\child\file.log")]
    public void DescendantCheck_AcceptsOnlySeparatorBoundedPaths(string path)
        => Assert.Equal(path, FixtureStore.RequireDescendant(FixtureStore.AllowedUncRoot, path));

    [Theory]
    [InlineData(@"\\mint-serv\Plex Media\Test")]
    [InlineData(@"\\mint-serv\Plex Media\Testing\file.log")]
    [InlineData(@"\\mint-serv\Plex Media\Test\..\sibling\file.log")]
    [InlineData(@"\\mint-serv\Plex Media\other\file.log")]
    [InlineData(@"\\other\share\Test\file.log")]
    [InlineData(@"C:\Test\file.log")]
    public void DescendantCheck_RejectsRootParentSiblingAndOtherHostsWithoutAccess(string path)
        => Assert.Throws<InvalidOperationException>(() => FixtureStore.RequireDescendant(FixtureStore.AllowedUncRoot, path));

    [Fact]
    public async Task FixturePreparation_RejectsRemoteOutputBeforeAnyAccess()
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FixtureStore.PrepareAsync(@"\\other\share\new-fixture", false, default));

    [Theory]
    [InlineData(@"\\other\share\file.log")]
    [InlineData("//other/share/file.log")]
    public async Task EvidenceCapture_RejectsUnauthorizedUncBeforeAnyAccess(string path)
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FixtureStore.CaptureAsync("unc-test", path, FileEncoding.Utf8, true, default));

    [Fact]
    public async Task FixturePreparation_DoesNotOverwriteExistingEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "WeezTail-unc-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sentinel = Path.Combine(root, "sentinel");
            await File.WriteAllTextAsync(sentinel, "preserve");
            await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureStore.PrepareAsync(root, false, default));
            Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("append")]
    [InlineData("rewrite")]
    [InlineData("replacement")]
    public async Task FixtureEvidence_RejectsChangedContentOrGeneration(string mutation)
    {
        var root = Path.Combine(Path.GetTempPath(), "WeezTail-unc-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "fixture.log");
            await File.WriteAllTextAsync(path, "needle one\n");
            var fixture = await FixtureStore.CaptureAsync("local-test", path, FileEncoding.Utf8, true, default);
            await FixtureStore.VerifyAsync(fixture, true, default);
            if (mutation == "append")
                await File.AppendAllTextAsync(path, "two\n");
            else if (mutation == "rewrite")
            {
                await File.WriteAllTextAsync(path, "needle two\n");
                File.SetLastWriteTimeUtc(path, new DateTime(fixture.WriteTicks, DateTimeKind.Utc));
            }
            else
            {
                var replacement = Path.Combine(root, "replacement.log");
                await File.WriteAllTextAsync(replacement, "needle one\n");
                File.SetLastWriteTimeUtc(replacement, new DateTime(fixture.WriteTicks, DateTimeKind.Utc));
                File.Move(replacement, path, overwrite: true);
            }
            await Assert.ThrowsAsync<IOException>(() => FixtureStore.VerifyAsync(fixture, true, default));
        }
        finally { Directory.Delete(root, true); }
    }
}
