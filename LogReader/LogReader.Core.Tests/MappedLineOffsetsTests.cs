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

    [Fact]
    public void FlushOverflow_MappingFailure_PreservesExistingMappingAndOverflow()
    {
        var mappingCalls = 0;
        using var offsets = new MappedLineOffsets(
            (path, length) =>
            {
                if (++mappingCalls == 2)
                    throw new IOException("expanded mapping failed");

                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                try
                {
                    return MemoryMappedFile.CreateFromFile(
                        stream,
                        mapName: null,
                        length,
                        MemoryMappedFileAccess.Read,
                        HandleInheritability.None,
                        leaveOpen: false);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            },
            static (mapping, length) => mapping.CreateViewAccessor(
                0,
                length,
                MemoryMappedFileAccess.Read));
        offsets.Add(0);
        offsets.Add(12);
        offsets.Freeze();

        for (var i = 0; i < MappedLineOffsets.OverflowFlushThreshold - 1; i++)
            offsets.Add(i + 13);

        Assert.Throws<IOException>(() => offsets.Add(10_000));

        Assert.Equal(MappedLineOffsets.OverflowFlushThreshold + 2, offsets.Count);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(12, offsets[1]);
        Assert.Equal(13, offsets[2]);
        Assert.Equal(10_000, offsets[^1]);
        Assert.Single(Directory.GetFiles(AppPaths.IndexDirectory, "idx_*.bin"));

        offsets.Add(10_001);

        Assert.Equal(MappedLineOffsets.OverflowFlushThreshold + 3, offsets.Count);
        Assert.Equal(10_000, offsets[^2]);
        Assert.Equal(10_001, offsets[^1]);
    }

    public void Dispose()
    {
        _appPathsScope.Dispose();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
