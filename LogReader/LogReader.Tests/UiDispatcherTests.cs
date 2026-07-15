namespace LogReader.Tests;

using LogReader.App.Helpers;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class UiDispatcherTests
{
    [Fact]
    public async Task FileSession_UsesInjectedDispatcherForUiMutationFallback()
    {
        var dispatcher = new RecordingUiDispatcher();
        var session = new FileSession(
            new FileSessionKey(@"C:\logs\app.log", FileEncoding.Utf8),
            new StubLogReaderService(),
            new StubFileTailService(),
            new StubEncodingDetectionService(),
            dispatcher);

        await session.InvokeOnSessionContextAsync(() => session.SetTotalLinesForTesting(42));

        Assert.Equal(1, dispatcher.ActionInvocationCount);
        Assert.Equal(42, session.TotalLines);
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        public int ActionInvocationCount { get; private set; }

        public bool CheckAccess() => false;

        public Task InvokeAsync(Action action)
        {
            ActionInvocationCount++;
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action)
        {
            ActionInvocationCount++;
            return action();
        }
    }

    private sealed class StubLogReaderService : ILogReaderService
    {
        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubFileTailService : IFileTailService
    {
        public event EventHandler<TailEventArgs>? LinesAppended
        {
            add { }
            remove { }
        }

        public event EventHandler<FileRotatedEventArgs>? FileRotated
        {
            add { }
            remove { }
        }

        public event EventHandler<TailErrorEventArgs>? TailError
        {
            add { }
            remove { }
        }

        public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250)
        {
        }

        public void StopTailing(string filePath)
        {
        }

        public void StopAll()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubEncodingDetectionService : IEncodingDetectionService
    {
        public FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8)
            => fallback;

        public EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding)
            => EncodingHelper.ResolveManualEncodingDecision(selectedEncoding);
    }
}
