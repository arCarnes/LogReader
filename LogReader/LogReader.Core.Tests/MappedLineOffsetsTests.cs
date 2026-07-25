namespace LogReader.Core.Tests;

using System.IO.MemoryMappedFiles;
using LogReader.Core;
using LogReader.Core.Models;

public sealed class MappedLineOffsetsTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"WeezTailOffsets_{Guid.NewGuid():N}");
    private readonly IDisposable _appPathsScope;

    public MappedLineOffsetsTests()
    {
        Directory.CreateDirectory(_testRoot);
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testRoot);
    }

    [Fact]
    public void Freeze_MappingFailure_RemovesTemporaryIndexFile()
    {
        using var offsets = new MappedLineOffsets(
            (_, _) => throw new IOException("mapping failed"),
            static (mapping, length) => mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read));
        offsets.Add(0);
        offsets.Add(12);

        Assert.Throws<IOException>(() => offsets.Freeze());

        Assert.Empty(Directory.GetFiles(AppPaths.IndexDirectory, "idx_*.bin"));
    }

    [Fact]
    public void Freeze_AccessorFailure_RemovesTemporaryIndexFile()
    {
        using var offsets = new MappedLineOffsets(
            static (path, length) => MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                mapName: null,
                length,
                MemoryMappedFileAccess.Read),
            (_, _) => throw new IOException("accessor failed"));
        offsets.Add(0);
        offsets.Add(12);

        Assert.Throws<IOException>(() => offsets.Freeze());

        Assert.Empty(Directory.GetFiles(AppPaths.IndexDirectory, "idx_*.bin"));
    }

    public void Dispose()
    {
        _appPathsScope.Dispose();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
