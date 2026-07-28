namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class SessionThreadingLifetimeTests
{
    [Fact]
    public async Task UpdateLineIndexLineCountAsync_WaitsForOutstandingReadLease()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        using var tab = CreateTab(reader);
        await tab.LoadAsync();

        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.AppendLine("Line 4");
        var updateTask = tab.UpdateLineIndexLineCountAsync(CancellationToken.None);

        Assert.NotSame(updateTask, await Task.WhenAny(updateTask, Task.Delay(100)));

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));

        var updatedLineCount = await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, updatedLineCount);
        Assert.Equal(4, tab.TotalLines);
    }

    [Fact]
    public async Task UpdateLineIndexLineCountAsync_DisposesRetiredIndexAfterReplacement()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        var oldIndex = tab.ActiveSession.DebugLineIndex;
        Assert.NotNull(oldIndex);

        reader.ReplaceLines(Enumerable.Range(1, 2).Select(i => $"New {i}"));

        var updatedLineCount = await tab.UpdateLineIndexLineCountAsync(CancellationToken.None);

        Assert.Equal(2, updatedLineCount);
        Assert.NotSame(oldIndex, tab.ActiveSession.DebugLineIndex);
        Assert.True(IsDisposed(oldIndex!.LineOffsets));
    }

    [Fact]
    public async Task UpdateLineIndexLineCountAsync_DoesNotDisposeIndexWhenServiceReturnsSameInstance()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"))
        {
            ReturnExistingIndexOnUpdate = true
        };
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        var oldIndex = tab.ActiveSession.DebugLineIndex;
        Assert.NotNull(oldIndex);

        var updatedLineCount = await tab.UpdateLineIndexLineCountAsync(CancellationToken.None);

        Assert.Null(updatedLineCount);
        Assert.Same(oldIndex, tab.ActiveSession.DebugLineIndex);
        Assert.False(IsDisposed(oldIndex!.LineOffsets));
    }

    [Fact]
    public async Task ResetLineIndexAsync_WaitsForOutstandingReadLeaseBeforeClearingIndex()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        using var tab = CreateTab(reader);
        await tab.LoadAsync();

        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resetTask = tab.ResetLineIndexAsync();
        Assert.NotSame(resetTask, await Task.WhenAny(resetTask, Task.Delay(100)));

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));
        await resetTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(tab.ActiveSession.HasNoLineIndex);
        Assert.Equal(1, tab.SearchContentVersion);
    }

    [Fact]
    public async Task FileRotated_ReloadWaitsForOutstandingReadLeaseBeforePublishingNewState()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();

        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.ReplaceLines(Enumerable.Range(1, 5).Select(i => $"Rotated {i}"));
        await Task.Run(() => tailService.RaiseFileRotated(tab.FilePath));

        await Task.Delay(100);
        Assert.Equal(3, tab.TotalLines);

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForAsync(() => tab.TotalLines == 5 && tab.VisibleLines.LastOrDefault()?.LineNumber == 5);
        Assert.Equal("Rotated 5", tab.VisibleLines.Last().Text);
    }

    [Fact]
    public async Task AppendThenRotation_CommitsReplacementAfterOlderAppendWork()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Old {i}"));
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        reader.BlockNextUpdate();

        reader.AppendLine("Old 4");
        tailService.RaiseLinesAppended(tab.FilePath);
        await reader.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.ReplaceLines(new[] { "New 1", "New 2" });
        tailService.RaiseFileRotated(tab.FilePath);
        await Task.Delay(100);
        Assert.Equal(0, tab.SearchContentVersion);

        reader.ReleaseBlockedUpdate();

        await WaitForAsync(
            () => tab.SearchContentVersion == 1 &&
                  tab.TotalLines == 2 &&
                  tab.VisibleLines.LastOrDefault()?.Text == "New 2",
            () => $"Version={tab.SearchContentVersion}, Total={tab.TotalLines}, Last={tab.VisibleLines.LastOrDefault()?.Text}, Status={tab.StatusText}");
        Assert.Equal(new[] { "New 1", "New 2" }, tab.VisibleLines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public async Task RotationThenAppend_WaitsForReloadBeforeApplyingNewGenerationAppend()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Old {i}"));
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        var updateCountBeforeRotation = reader.UpdateIndexCallCount;

        reader.ReplaceLines(new[] { "New 1", "New 2" });
        reader.BlockNextUpdate();
        tailService.RaiseFileRotated(tab.FilePath);
        await reader.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.AppendLine("New 3");
        tailService.RaiseLinesAppended(tab.FilePath);
        await Task.Delay(100);
        Assert.Equal(updateCountBeforeRotation + 1, reader.UpdateIndexCallCount);

        reader.ReleaseBlockedUpdate();

        await WaitForAsync(
            () => tab.SearchContentVersion == 1 &&
                  tab.TotalLines == 3 &&
                  tab.VisibleLines.LastOrDefault()?.Text == "New 3",
            () => $"Version={tab.SearchContentVersion}, Total={tab.TotalLines}, Last={tab.VisibleLines.LastOrDefault()?.Text}, Status={tab.StatusText}, Updates={reader.UpdateIndexCallCount}");
        Assert.Equal(new[] { "New 1", "New 2", "New 3" }, tab.VisibleLines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public async Task FileRotated_UsesHintedUpdateWithoutStartingAnotherBuild()
    {
        var reader = new MutableLogReaderService(new[] { "Old 1", "Old 2" });
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        var buildsBeforeRotation = reader.BuildIndexCallCount;
        var updatesBeforeRotation = reader.UpdateIndexCallCount;

        reader.ReplaceLines(new[] { "New 1", "New 2", "New 3" });
        tailService.RaiseFileRotated(tab.FilePath);

        await WaitForAsync(() =>
            reader.UpdateIndexCallCount == updatesBeforeRotation + 1 &&
            tab.SearchContentVersion == 1);
        Assert.Equal(buildsBeforeRotation, reader.BuildIndexCallCount);
        Assert.Equal(FileChangeHint.UnspecifiedReplacement, reader.LastUpdateChangeHint);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FileRotated_LegacyReaderDefaultHintedUpdate_RebuildsAndRefreshes(
        int replacementLineCount)
    {
        var reader = new LegacyRotationLogReaderService(
            Enumerable.Range(1, 3).Select(i => $"Old {i}"));
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        var buildsBeforeRotation = reader.BuildIndexCallCount;
        var replacementLines = Enumerable.Range(1, replacementLineCount)
            .Select(i => $"New {i}")
            .ToArray();

        reader.ReplaceLines(replacementLines);
        tailService.RaiseFileRotated(tab.FilePath);

        await WaitForAsync(() =>
            reader.BuildIndexCallCount == buildsBeforeRotation + 1 &&
            tab.SearchContentVersion == 1 &&
            tab.TotalLines == replacementLineCount &&
            tab.VisibleLines.Select(line => line.Text).SequenceEqual(replacementLines));
    }

    [Fact]
    public async Task FileRotated_SameMarkedIndex_RefreshesWithoutDisposingIndex()
    {
        var reader = new MutableLogReaderService(new[] { "Old 1", "Old 2" })
        {
            ReturnExistingIndexOnUpdate = true
        };
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        var existingIndex = tab.ActiveSession.DebugLineIndex;
        Assert.NotNull(existingIndex);

        reader.ReplaceLines(new[] { "New 1", "New 2" });
        tailService.RaiseFileRotated(tab.FilePath);

        await WaitForAsync(() =>
            tab.SearchContentVersion == 1 &&
            tab.VisibleLines.Select(line => line.Text)
                .SequenceEqual(new[] { "New 1", "New 2" }));
        Assert.Same(existingIndex, tab.ActiveSession.DebugLineIndex);
        Assert.False(IsDisposed(existingIndex!.LineOffsets));
    }

    [Fact]
    public async Task TailEventBurst_CoalescesToActiveUpdateAndOneFollowUp()
    {
        var reader = new MutableLogReaderService(new[] { "Line 1" });
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        var updatesBeforeBurst = reader.UpdateIndexCallCount;
        reader.BlockNextUpdate();

        reader.AppendLine("Line 2");
        tailService.RaiseLinesAppended(tab.FilePath);
        await reader.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 10_000; i++)
        {
            if (i % 10 == 0)
                tailService.RaiseFileRotated(tab.FilePath);
            else
                tailService.RaiseLinesAppended(tab.FilePath);
        }

        reader.ReleaseBlockedUpdate();
        await WaitForAsync(() => reader.UpdateIndexCallCount >= updatesBeforeBurst + 2);
        await Task.Delay(150);

        Assert.Equal(updatesBeforeBurst + 2, reader.UpdateIndexCallCount);
        Assert.Equal(FileChangeHint.UnspecifiedReplacement, reader.LastUpdateChangeHint);
    }

    [Fact]
    public async Task AutomaticReloadPause_SurvivesVisibilityChanges_AndExplicitRetryResumes()
    {
        var reader = new MutableLogReaderService(new[] { "Old 1", "Old 2" });
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();
        reader.BlockNextAutomaticReload(TimeSpan.FromMinutes(1));

        reader.ReplaceLines(new[] { "New 1" });
        tailService.RaiseFileRotated(tab.FilePath);
        await WaitForAsync(() => tab.IsAutomaticReloadPaused && tab.IsSuspended);
        var updatesAfterPause = reader.UpdateIndexCallCount;

        tab.OnBecameHidden();
        tab.OnBecameVisible();
        tab.ApplyVisibleTailingMode(500);
        await Task.Delay(150);

        Assert.True(tab.IsAutomaticReloadPaused);
        Assert.True(tab.IsSuspended);
        Assert.Equal(updatesAfterPause, reader.UpdateIndexCallCount);
        Assert.Contains("Automatic tailing paused", tab.StatusText, StringComparison.Ordinal);

        await tab.RetryAutomaticTailingCommand.ExecuteAsync(null);
        await WaitForAsync(() => !tab.IsAutomaticReloadPaused && !tab.IsSuspended);

        Assert.Equal(updatesAfterPause + 1, reader.UpdateIndexCallCount);
        Assert.Contains(tab.FilePath, tailService.ActiveFiles);
    }

    [Fact]
    public async Task Dispose_WaitsForOutstandingReadLeaseBeforeCleanupCompletes()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        var tab = CreateTab(reader);
        await tab.LoadAsync();

        var session = tab.ActiveSession;
        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tab.Dispose();

        var disposeTask = session.DebugLineIndexDisposeTask;
        Assert.NotNull(disposeTask);
        Assert.False(disposeTask!.IsCompleted);

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(session.DebugLineIndex);
    }

    [Fact]
    public async Task LinesAppended_PublishesViewportAndStatusOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(Enumerable.Range(1, 60).Select(i => $"Line {i}"));
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();

            var propertyThreads = new ConcurrentBag<int>();
            var collectionThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.TotalLines) or nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };
            tab.VisibleLines.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            reader.AppendLine("Line 61");
            await Task.Run(() => tailService.RaiseLinesAppended(tab.FilePath));

            await WaitForAsync(() => tab.TotalLines == 61 && tab.VisibleLines.LastOrDefault()?.LineNumber == 61);

            Assert.NotEmpty(propertyThreads);
            Assert.NotEmpty(collectionThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
            Assert.All(collectionThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task FileRotated_PublishesReloadedViewportOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();

            var propertyThreads = new ConcurrentBag<int>();
            var collectionThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.TotalLines) or nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };
            tab.VisibleLines.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            reader.ReplaceLines(Enumerable.Range(1, 5).Select(i => $"Rotated {i}"));
            await Task.Run(() => tailService.RaiseFileRotated(tab.FilePath));

            await WaitForAsync(() => tab.TotalLines == 5 && tab.VisibleLines.LastOrDefault()?.Text == "Rotated 5");

            Assert.NotEmpty(propertyThreads);
            Assert.NotEmpty(collectionThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
            Assert.All(collectionThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task TailError_PublishesSuspendedStateOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var tailService = new StubFileTailService();
            using var tab = CreateTab(new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}")), tailService: tailService);
            await tab.LoadAsync();

            var propertyThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.IsSuspended) or nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };

            await Task.Run(() => tailService.RaiseTailError(tab.FilePath, "worker failure"));

            await WaitForAsync(() => tab.IsSuspended && tab.StatusText == "Tailing stopped: worker failure");

            Assert.NotEmpty(propertyThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task LinesAppended_WithActiveFilter_PublishesFilteredViewportAndStatusOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(new[]
            {
                "INFO first",
                "ERROR second"
            });
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();
            await tab.ApplyFilterAsync(
                matchingLineNumbers: new[] { 2 },
                statusText: "Filter active: 1 matching lines.",
                filterRequest: new SearchRequest
                {
                    Query = "ERROR",
                    FilePaths = new List<string> { tab.FilePath },
                    SourceMode = SearchRequestSourceMode.SnapshotAndTail
                });

            var propertyThreads = new ConcurrentBag<int>();
            var collectionThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.StatusText) or nameof(LogTabViewModel.FilteredLineCount))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };
            tab.VisibleLines.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            reader.AppendLine("ERROR third");
            await Task.Run(() => tailService.RaiseLinesAppended(tab.FilePath));

            await WaitForAsync(() =>
                tab.FilteredLineCount == 2 &&
                tab.VisibleLines.LastOrDefault()?.LineNumber == 3 &&
                tab.StatusText == "Filter active (tailing): 2 matching lines.");

            Assert.NotEmpty(propertyThreads);
            Assert.NotEmpty(collectionThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
            Assert.All(collectionThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task LinesAppended_WithTimedOutRegexFilter_PausesFilterOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(new[]
            {
                "initial match",
                "ordinary line"
            });
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();
            await tab.ApplyFilterAsync(
                matchingLineNumbers: new[] { 1 },
                statusText: "Filter active: 1 matching lines.",
                filterRequest: new SearchRequest
                {
                    Query = @"(a+)+$",
                    IsRegex = true,
                    CaseSensitive = true,
                    FilePaths = new List<string> { tab.FilePath },
                    SourceMode = SearchRequestSourceMode.SnapshotAndTail
                });

            var propertyThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };

            reader.AppendLine(new string('a', 30) + "!");
            await Task.Run(() => tailService.RaiseLinesAppended(tab.FilePath));

            await WaitForAsync(() =>
                tab.TotalLines == 3 &&
                tab.FilteredLineCount == 1 &&
                tab.StatusText == LogFilterSession.TailRegexTimeoutStatusText);

            reader.AppendLine("safe later line");
            await Task.Run(() => tailService.RaiseLinesAppended(tab.FilePath));

            await WaitForAsync(() => tab.TotalLines == 4);
            Assert.True(tab.IsFilterActive);
            Assert.Equal(1, tab.FilteredLineCount);
            Assert.Equal(LogFilterSession.TailRegexTimeoutStatusText, tab.StatusText);
            Assert.NotEmpty(propertyThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task TimedOutRegexFilter_CloseAndReopen_PreservesPausedTailState()
    {
        var reader = new MutableLogReaderService(new[]
        {
            "initial match",
            "ordinary line"
        });
        var tailService = new StubFileTailService();
        RecentTabState recentState;
        using (var originalTab = CreateTab(reader, tailService: tailService))
        {
            await originalTab.LoadAsync();
            await originalTab.ApplyFilterAsync(
                matchingLineNumbers: new[] { 1 },
                statusText: "Filter active: 1 matching lines.",
                filterRequest: new SearchRequest
                {
                    Query = @"(a+)+$",
                    IsRegex = true,
                    CaseSensitive = true,
                    FilePaths = new List<string> { originalTab.FilePath },
                    SourceMode = SearchRequestSourceMode.SnapshotAndTail
                });

            reader.AppendLine(new string('a', 30) + "!");
            tailService.RaiseLinesAppended(originalTab.FilePath);
            await WaitForAsync(() => originalTab.StatusText == LogFilterSession.TailRegexTimeoutStatusText);
            recentState = originalTab.CaptureRecentState();
        }

        using var reopenedTab = CreateTab(reader, tailService: tailService);
        await reopenedTab.LoadAsync();
        await reopenedTab.RestoreRecentStateAsync(recentState);

        reader.AppendLine("aaaa");
        tailService.RaiseLinesAppended(reopenedTab.FilePath);
        await WaitForAsync(() => reopenedTab.TotalLines == 4);

        Assert.True(reopenedTab.IsFilterActive);
        Assert.Equal(1, reopenedTab.FilteredLineCount);
        Assert.Equal(LogFilterSession.TailRegexTimeoutStatusText, reopenedTab.StatusText);
    }

    [Fact]
    public async Task LoadAsync_AppendedLineDuringTailRegistrationIsCaughtUpOnce()
    {
        var reader = new MutableLogReaderService(new[] { "first", "second" });
        var tailService = new RegistrationRaceTailService(() => reader.AppendLine("third"));
        using var tab = CreateTab(reader, tailService);
        var publishedCatchUpCount = 0;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.TotalLines) && tab.TotalLines == 3)
                publishedCatchUpCount++;
        };

        await tab.LoadAsync();
        await WaitForAsync(() => reader.UpdateIndexCallCount >= 2);

        Assert.Equal(3, tab.TotalLines);
        Assert.Equal(1, publishedCatchUpCount);
        Assert.Equal(1, tab.VisibleLines.Count(line => line.LineNumber == 3));
        Assert.Equal("third", tab.VisibleLines.Single(line => line.LineNumber == 3).Text);
        Assert.Equal(1, tailService.StartCallCount);
    }

    [Fact]
    public async Task SwitchingLoadedTabBackToAuto_DoesNotRunDetectionSynchronouslyOnCallerThread()
    {
        var detectionService = new BlockingEncodingDetectionService();
        using var tab = new LogTabViewModel(
            "tab-auto",
            @"C:\test\encoding.log",
            new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}")),
            new StubFileTailService(),
            detectionService,
            new AppSettings(),
            skipInitialEncodingResolution: true,
            sessionRegistry: null,
            initialEncoding: FileEncoding.Utf16,
            scopeDashboardId: null);

        await tab.LoadAsync();

        detectionService.BlockAutoResolution();

        var reloadCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.IsLoading) && !tab.IsLoading)
                reloadCompleted.TrySetResult(true);
        };

        var setterThreadId = Environment.CurrentManagedThreadId;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        tab.Encoding = FileEncoding.Auto;
        stopwatch.Stop();

        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 250);
        Assert.Equal("Auto (UTF-8)", tab.SelectedEncodingDisplayLabel);
        Assert.Equal("Auto -> UTF-8 (fallback)", tab.EncodingStatusText);

        await detectionService.AutoResolveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(setterThreadId, detectionService.ResolveThreadIds);

        detectionService.ReleaseAutoResolution();
        await reloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FileEncoding.Auto, tab.Encoding);
        Assert.Equal(FileEncoding.Utf8, tab.EffectiveEncoding);
    }

    [Fact]
    public async Task FilterSession_ViewportSnapshotCache_ReusesUntilFilterMutates()
    {
        var filterSession = new LogFilterSession();
        filterSession.ApplyFilter(
            matchingLineNumbers: new[] { 2 },
            statusText: "Filter active: 1 matching lines.",
            filterRequest: new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { @"C:\test\file.log" },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            hasParseableTimestamps: false,
            totalLines: 2);

        var snapshot1 = filterSession.ViewportFilteredLineNumbersSnapshot;
        var snapshot2 = filterSession.ViewportFilteredLineNumbersSnapshot;

        Assert.NotNull(snapshot1);
        Assert.Same(snapshot1, snapshot2);

        var updated = await filterSession.ProcessAppendedLinesAsync(
            updatedLineCount: 3,
            lineIndex: new LineIndex { FilePath = @"C:\test\file.log", FileSize = 300 },
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR third" }),
            retainedDisplayLineLimit: 10,
            ct: CancellationToken.None);

        Assert.True(updated.HasChanges);

        var snapshot3 = filterSession.ViewportFilteredLineNumbersSnapshot;
        Assert.NotNull(snapshot3);
        Assert.NotSame(snapshot1, snapshot3);
        Assert.Equal(new[] { 2, 3 }, snapshot3);
    }

    private static LogTabViewModel CreateTab(
        ILogReaderService logReader,
        IFileTailService? tailService = null,
        IEncodingDetectionService? encodingDetectionService = null)
        => new(
            "test-id",
            @"C:\test\file.log",
            logReader,
            tailService ?? new StubFileTailService(),
            encodingDetectionService ?? new FileEncodingDetectionService(),
            new AppSettings());

    private static async Task WaitForAsync(Func<bool> condition, Func<string>? describeState = null)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException(
                    describeState == null
                        ? "Condition was not met within the allotted time."
                        : $"Condition was not met within the allotted time. {describeState()}");

            await Task.Delay(25);
        }
    }

    private static bool IsDisposed(MappedLineOffsets offsets)
    {
        var field = typeof(MappedLineOffsets).GetField(
            "_disposed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (bool)field!.GetValue(offsets)!;
    }

    private sealed class MutableLogReaderService : ILogReaderService
    {
        private readonly object _gate = new();
        private List<string> _lines;

        public MutableLogReaderService(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public bool ReturnExistingIndexOnUpdate { get; init; }

        public int BuildIndexCallCount { get; private set; }

        public int UpdateIndexCallCount { get; private set; }

        public FileChangeHint LastUpdateChangeHint { get; private set; }

        public TaskCompletionSource<bool> UpdateStarted { get; private set; } = CreateSignal();

        private TaskCompletionSource<bool>? _releaseUpdate;
        private AutomaticReloadBlockedException? _nextAutomaticReloadFailure;

        public void BlockNextUpdate()
        {
            UpdateStarted = CreateSignal();
            _releaseUpdate = CreateSignal();
        }

        public void ReleaseBlockedUpdate()
            => _releaseUpdate?.TrySetResult(true);

        public void BlockNextAutomaticReload(TimeSpan retryAfter)
            => _nextAutomaticReloadFailure = new AutomaticReloadBlockedException(
                "Automatic reload blocked for testing.",
                retryAfter);

        public void AppendLine(string line)
        {
            lock (_gate)
                _lines.Add(line);
        }

        public void ReplaceLines(IEnumerable<string> lines)
        {
            lock (_gate)
                _lines = lines.ToList();
        }

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            BuildIndexCallCount++;
            return Task.FromResult(CreateIndex(filePath));
        }

        public async Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => await UpdateIndexAsync(
                filePath,
                existingIndex,
                encoding,
                FileChangeHint.None,
                ct);

        public async Task<LineIndex> UpdateIndexAsync(
            string filePath,
            LineIndex existingIndex,
            FileEncoding encoding,
            FileChangeHint changeHint,
            CancellationToken ct = default)
        {
            UpdateIndexCallCount++;
            LastUpdateChangeHint = changeHint;
            var automaticReloadFailure = _nextAutomaticReloadFailure;
            if (automaticReloadFailure != null)
            {
                _nextAutomaticReloadFailure = null;
                throw automaticReloadFailure;
            }

            var index = ReturnExistingIndexOnUpdate ? existingIndex : CreateIndex(filePath);
            if (changeHint != FileChangeHint.None)
                index.ReplacesPriorGeneration = true;

            var releaseUpdate = _releaseUpdate;
            if (releaseUpdate != null)
            {
                UpdateStarted.TrySetResult(true);
                await releaseUpdate.Task.WaitAsync(ct);
                _releaseUpdate = null;
            }

            return index;
        }

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            List<string> snapshot;
            lock (_gate)
                snapshot = _lines.ToList();

            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, snapshot.Count - boundedStart));
            return Task.FromResult<IReadOnlyList<string>>(snapshot.Skip(boundedStart).Take(boundedCount).ToList());
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            List<string> snapshot;
            lock (_gate)
                snapshot = _lines.ToList();

            if (lineNumber < 0 || lineNumber >= snapshot.Count)
                return Task.FromResult(string.Empty);

            return Task.FromResult(snapshot[lineNumber]);
        }

        private LineIndex CreateIndex(string filePath)
        {
            List<string> snapshot;
            lock (_gate)
                snapshot = _lines.ToList();

            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = snapshot.Count * 100
            };

            for (var i = 0; i < snapshot.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }

        private static TaskCompletionSource<bool> CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RegistrationRaceTailService : IFileTailService
    {
        private readonly Action _onStart;

        public RegistrationRaceTailService(Action onStart)
        {
            _onStart = onStart;
        }

        public event EventHandler<TailEventArgs>? LinesAppended;
#pragma warning disable CS0067
        public event EventHandler<FileRotatedEventArgs>? FileRotated;
        public event EventHandler<FileAvailabilityChangedEventArgs>? FileAvailabilityChanged;
        public event EventHandler<TailErrorEventArgs>? TailError;
#pragma warning restore CS0067

        public int StartCallCount { get; private set; }

        public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250)
        {
            StartCallCount++;
            _onStart();
            LinesAppended?.Invoke(this, new TailEventArgs { FilePath = filePath });
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

    private sealed class BlockingEncodingDetectionService : IEncodingDetectionService
    {
        private readonly TaskCompletionSource<bool> _autoResolveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _releaseAutoResolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _blockAutoResolution;

        public Task AutoResolveStarted => _autoResolveStarted.Task;

        public ConcurrentBag<int> ResolveThreadIds { get; } = new();

        public void BlockAutoResolution() => _blockAutoResolution = true;

        public void ReleaseAutoResolution() => _releaseAutoResolution.TrySetResult(true);

        public FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8)
            => FileEncoding.Utf8;

        public EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding)
        {
            ResolveThreadIds.Add(Environment.CurrentManagedThreadId);

            if (selectedEncoding != FileEncoding.Auto)
                return EncodingHelper.ResolveManualEncodingDecision(selectedEncoding);

            if (_blockAutoResolution)
            {
                _autoResolveStarted.TrySetResult(true);
                _releaseAutoResolution.Task.GetAwaiter().GetResult();
            }

            return new EncodingHelper.EncodingDecision(
                FileEncoding.Auto,
                FileEncoding.Utf8,
                "Auto -> UTF-8");
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private Func<Task>? _asyncAction;

        private SingleThreadSynchronizationContext()
        {
            _thread = new Thread(RunOnCurrentThread)
            {
                IsBackground = true,
                Name = nameof(SingleThreadSynchronizationContext)
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public static async Task RunAsync(Func<Task> asyncAction)
        {
            using var context = new SingleThreadSynchronizationContext
            {
                _asyncAction = asyncAction
            };
            context._thread.Start();
            await context._completion.Task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == _thread)
            {
                d(state);
                return;
            }

            using var signal = new ManualResetEventSlim();
            Exception? exception = null;
            Post(_ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    signal.Set();
                }
            }, null);

            signal.Wait();
            if (exception != null)
                ExceptionDispatchInfo.Capture(exception).Throw();
        }

        public void Dispose()
        {
            CompleteQueue();
            if (_thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void RunOnCurrentThread()
        {
            var previousContext = Current;
            SetSynchronizationContext(this);
            try
            {
                Task asyncTask;
                try
                {
                    asyncTask = _asyncAction!();
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                    return;
                }

                asyncTask.ContinueWith(
                    static (task, state) =>
                    {
                        var context = (SingleThreadSynchronizationContext)state!;
                        if (task.IsFaulted)
                            context._completion.TrySetException(task.Exception!.InnerExceptions);
                        else if (task.IsCanceled)
                            context._completion.TrySetCanceled();
                        else
                            context._completion.TrySetResult();

                        context.CompleteQueue();
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);

                foreach (var workItem in _queue.GetConsumingEnumerable())
                    workItem.Callback(workItem.State);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                CompleteQueue();
            }
            finally
            {
                SetSynchronizationContext(previousContext);
            }
        }

        private void CompleteQueue()
        {
            if (!_queue.IsAddingCompleted)
                _queue.CompleteAdding();
        }
    }

    private sealed class LegacyRotationLogReaderService : ILogReaderService
    {
        private readonly object _gate = new();
        private List<string> _lines;

        public LegacyRotationLogReaderService(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public int BuildIndexCallCount { get; private set; }

        public void ReplaceLines(IEnumerable<string> lines)
        {
            lock (_gate)
                _lines = lines.ToList();
        }

        public Task<LineIndex> BuildIndexAsync(
            string filePath,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            BuildIndexCallCount++;
            return Task.FromResult(CreateIndex(filePath, GetLineSnapshot().Count));
        }

        public Task<LineIndex> UpdateIndexAsync(
            string filePath,
            LineIndex existingIndex,
            FileEncoding encoding,
            CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            var lines = GetLineSnapshot();
            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(
                0,
                Math.Min(count, lines.Count - boundedStart));
            return Task.FromResult<IReadOnlyList<string>>(
                lines.Skip(boundedStart).Take(boundedCount).ToList());
        }

        public Task<string> ReadLineAsync(
            string filePath,
            LineIndex index,
            int lineNumber,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            var lines = GetLineSnapshot();
            return Task.FromResult(
                lineNumber >= 0 && lineNumber < lines.Count
                    ? lines[lineNumber]
                    : string.Empty);
        }

        private List<string> GetLineSnapshot()
        {
            lock (_gate)
                return _lines.ToList();
        }

        private static LineIndex CreateIndex(string filePath, int lineCount)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = lineCount * 100
            };
            for (var i = 0; i < lineCount; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }
}
