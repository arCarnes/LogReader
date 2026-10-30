namespace LogReader.Tests;

using LogReader.App.Helpers;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class UiDispatcherTests
{
    [Fact]
    public async Task FileSession_UsesInjectedDispatcherForUiMutationFallback()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = new RecordingUiDispatcher();
            using var session = WithoutContext(() => new FileSession(
                new FileSessionKey(@"C:\logs\app.log", FileEncoding.Utf8),
                new StubLogReaderService(), new StubFileTailService(), new StubEncodingDetectionService(), dispatcher));
            try
            {
                await session.InvokeOnSessionContextAsync(() => session.SetTotalLinesForTesting(42));
                Assert.Equal(1, dispatcher.ActionInvocationCount);
                Assert.Equal(42, session.TotalLines);
            }
            finally
            {
                session.Dispose();
                await session.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        private int _actionInvocationCount;

        public int ActionInvocationCount => Volatile.Read(ref _actionInvocationCount);

        public bool CheckAccess() => false;

        public Task InvokeAsync(Action action)
        {
            Interlocked.Increment(ref _actionInvocationCount);
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action)
        {
            Interlocked.Increment(ref _actionInvocationCount);
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

        public event EventHandler<FileAvailabilityChangedEventArgs>? FileAvailabilityChanged
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
