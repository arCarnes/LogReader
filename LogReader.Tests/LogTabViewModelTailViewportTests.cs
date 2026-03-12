namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class LogTabViewModelTailViewportTests
{
    private sealed class RecordingAppendableLogReader : ILogReaderService
    {
        private readonly List<string> _lines;

        public RecordingAppendableLogReader(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public List<(int StartLine, int Count)> ReadLinesRequests { get; } = new();

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            ReadLinesRequests.Add((startLine, count));
            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lines.Count - boundedStart));
            var slice = _lines.Skip(boundedStart).Take(boundedCount).ToList();
            return Task.FromResult<IReadOnlyList<string>>(slice);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            if (lineNumber < 0 || lineNumber >= _lines.Count)
                return Task.FromResult(string.Empty);

            return Task.FromResult(_lines[lineNumber]);
        }

        private LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lines.Count * 100
            };

            for (var i = 0; i < _lines.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    [Fact]
    public async Task ResumeTailingWithCatchUp_AutoScroll_AppendsViewportInPlace()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 60).Select(i => $"Line {i}"));
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new AppSettings());

        await tab.LoadAsync();
        Assert.Equal(60, tab.TotalLines);
        Assert.Equal(50, tab.VisibleLines.Count);
        Assert.Equal(11, tab.VisibleLines.First().LineNumber);
        Assert.Equal(60, tab.VisibleLines.Last().LineNumber);

        var requestCountAfterLoad = reader.ReadLinesRequests.Count;

        reader.AppendLine("Line 61");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpIfAllowedAsync(globalAutoTailEnabled: true, pollingIntervalMs: 250);

        Assert.Equal(61, tab.TotalLines);
        Assert.Equal(50, tab.VisibleLines.Count);
        Assert.Equal(12, tab.VisibleLines.First().LineNumber);
        Assert.Equal(61, tab.VisibleLines.Last().LineNumber);

        var resumeRequests = reader.ReadLinesRequests.Skip(requestCountAfterLoad).ToList();
        Assert.Contains(resumeRequests, request => request.StartLine == 60 && request.Count == 1);
        Assert.DoesNotContain(resumeRequests, request => request.StartLine == 11 && request.Count == 50);
    }
}
