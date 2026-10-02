namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class AutomaticReloadRecoveryTests
{
    [Fact]
    public async Task OrdinaryAppendIo_RecoversWithoutAReplacementHintOrAnotherWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.LineCount = 2;
        fixture.Reader.Failures.Enqueue(new IOException("identity temporarily unavailable"));
        fixture.Tail.RaiseLinesAppended(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Session.IsAutomaticReloadPaused && fixture.Clock.ActiveTimers == 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(2, fixture.Session.TotalLines);
        Assert.Equal(FileChangeHint.None, fixture.Reader.LastHint);
        Assert.Equal(0, fixture.Reader.Scans);
        Assert.Equal((1, 2), fixture.Client.Advances.Single());
    }

    [Fact]
    public async Task AppendNotificationIo_MergesUnpublishedCountsWithoutRescanning()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Client.Failures.Enqueue(new IOException("temporary viewport read failure"));
        fixture.Reader.LineCount = 2;
        fixture.Tail.RaiseLinesAppended(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Session.IsAutomaticReloadPaused && fixture.Clock.ActiveTimers == 1);
        fixture.Reader.LineCount = 3;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal((1, 3), fixture.Client.Advances.Single());
        Assert.Equal(0, fixture.Reader.Scans);
        Assert.Equal(0, fixture.Client.Reloads);
    }

    [Fact]
    public async Task AppendToCommittedReplacement_DoesNotRepeatGenerationReset()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Generation++;
        fixture.Tail.RaiseFileRotated(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Client.Reloads == 1);
        var version = fixture.Session.SearchContentVersion;
        fixture.Reader.LineCount = 2;
        fixture.Tail.RaiseLinesAppended(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Client.Advances.Count == 1);
        Assert.Equal(version, fixture.Session.SearchContentVersion);
        Assert.Equal(1, fixture.Client.Reloads);
        Assert.Equal((1, 2), fixture.Client.Advances.Single());
    }

    [Fact]
    public async Task Cooldown_RecoversAtDeadlineWithoutClearingAdmissionDelay()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Session.DebugLineIndex!.AutomaticReloadNotBeforeTimestamp = 42;
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Delayed", TimeSpan.FromSeconds(30),
            reason: AutomaticReloadReason.Cooldown));
        await fixture.RotateAndWaitAsync();
        var updates = fixture.Reader.Updates;
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(updates, fixture.Reader.Updates);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(42, fixture.Reader.LastAdmissionTimestamp);
        Assert.Contains(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
        Assert.Equal(1, fixture.Client.Reloads);
        Assert.Null(fixture.Session.AutomaticReloadStatusText);
    }

    [Fact]
    public async Task FailedFirstScan_PreservesReasonWhenLaterAttemptHitsCooldown()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Replacement scan failed", TimeSpan.FromSeconds(30),
            new IOException("sharing violation"), AutomaticReloadReason.ReloadFailed));
        await fixture.RotateAndWaitAsync();
        Assert.Contains("Replacement scan failed", fixture.Session.AutomaticReloadStatusText);
        Assert.Contains("sharing violation", fixture.Session.AutomaticReloadFailureDetail);
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Delayed", TimeSpan.FromSeconds(30),
            reason: AutomaticReloadReason.Cooldown));
        var updates = fixture.Reader.Updates;
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitAsync(() => fixture.Reader.Updates == updates + 1 && fixture.Clock.ActiveTimers == 1);
        Assert.Contains("Replacement scan failed", fixture.Session.AutomaticReloadStatusText);
        Assert.DoesNotContain("prevent repeated", fixture.Session.AutomaticReloadStatusText);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
    }

    [Fact]
    public async Task MetadataInstability_BackoffCapsAndSuccessfulRecoveryResetsIt()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        foreach (var seconds in new[] { 2, 5, 15, 30, 60, 120, 300, 300 })
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), fixture.Clock.NextDelay);
            fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
            var updates = fixture.Reader.Updates;
            fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
            await WaitAsync(() => fixture.Reader.Updates == updates + 1 && fixture.Clock.ActiveTimers == 1);
        }
        fixture.Clock.Advance(TimeSpan.FromSeconds(300));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable again"));
        await fixture.RotateAndWaitAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), fixture.Clock.NextDelay);
    }

    [Fact]
    public async Task HiddenOrDetachedSession_DefersUntilAVisibleClientReturns()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        fixture.Client.Visible = false;
        fixture.Session.SuspendTailingIfNoVisibleClients();
        await WaitAsync(() => fixture.Clock.ActiveTimers == 0);
        Assert.Contains("visible", fixture.Session.AutomaticReloadStatusText);
        var updates = fixture.Reader.Updates;
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(updates, fixture.Reader.Updates);
        fixture.Session.DetachClient(fixture.Client);
        fixture.Client.Visible = true;
        fixture.Session.AttachClient(fixture.Client);
        fixture.Session.ResumeTailing();
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(updates + 2, fixture.Reader.Updates);
    }

    [Fact]
    public async Task SharedVisibleClients_UseOneWorkerWhenAnotherClientHides()
    {
        using var fixture = await Fixture.CreateAsync();
        var second = new Client();
        fixture.Session.AttachClient(second);
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        fixture.Client.Visible = false;
        fixture.Session.SuspendTailingIfNoVisibleClients();
        fixture.Session.ResumeTailing();
        fixture.Session.ApplyVisibleTailingMode(500);
        Assert.Equal(1, fixture.Clock.ActiveTimers);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(1, fixture.Reader.Scans);
        Assert.Equal(1, second.Reloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedOrCapacityFailure_RequiresManualRetry(bool capacity)
    {
        using var fixture = await Fixture.CreateAsync();
        Exception failure = capacity ? new LineIndexCapacityExceededException(1) : new InvalidOperationException("unexpected");
        fixture.Reader.Failures.Enqueue(failure);
        fixture.Reader.Generation++;
        fixture.Tail.RaiseFileRotated(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Session.IsAutomaticReloadPaused && fixture.Session.AutomaticReloadStatusText != null);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        Assert.Contains("manually", fixture.Session.AutomaticReloadStatusText);
        fixture.Session.DebugLineIndex!.AutomaticReloadNotBeforeTimestamp = 42;
        await fixture.Session.RetryAutomaticTailingAsync();
        Assert.False(fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(0, fixture.Reader.LastAdmissionTimestamp);
    }

    [Fact]
    public async Task IoBeforeReplacementAdmission_EntersRecovery()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new IOException("replacement temporarily absent"));
        await fixture.RotateAndWaitAsync();
        Assert.Contains("replacement temporarily absent", fixture.Session.AutomaticReloadFailureDetail);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(1, fixture.Client.Reloads);
    }

    [Fact]
    public async Task NotificationIo_RevalidatesNewGenerationAndPreservesPendingReloadThroughCooldown()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Client.Failures.Enqueue(new IOException("file changed before indexed lines could be read"));
        fixture.Reader.Generation++;
        fixture.Tail.RaiseFileRotated(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Session.IsAutomaticReloadPaused && fixture.Clock.ActiveTimers == 1);
        Assert.Equal(1, fixture.Reader.Scans);
        fixture.Reader.Generation++;
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Delayed", TimeSpan.FromSeconds(30),
            reason: AutomaticReloadReason.Cooldown));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => fixture.Clock.NextDelay == TimeSpan.FromSeconds(30));
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(2, fixture.Reader.Scans);
        Assert.Equal(FileChangeHint.None, fixture.Reader.LastHint);
        Assert.Equal(1, fixture.Client.Reloads);
        Assert.Equal(fixture.Reader.Generation, fixture.Session.CurrentGenerationToken.FileId);
    }

    [Fact]
    public async Task NotificationIo_UnchangedGenerationDoesNotRescanCommittedIndex()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Client.Failures.Enqueue(new IOException("temporary read failure"));
        fixture.Reader.Generation++;
        fixture.Tail.RaiseFileRotated(fixture.Session.FilePath);
        await WaitAsync(() => fixture.Session.IsAutomaticReloadPaused && fixture.Clock.ActiveTimers == 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(1, fixture.Reader.Scans);
        Assert.Equal(1, fixture.Client.Reloads);
    }

    [Fact]
    public async Task ManualRetryRacingScheduledScan_DoesNotStartAnotherScan()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        fixture.Reader.BlockNextUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Reader.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = fixture.Session.RetryAutomaticTailingAsync();
        fixture.Reader.BlockNextUpdate.TrySetResult();
        await manual;
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused && fixture.Clock.ActiveTimers == 0);
        Assert.Equal(1, fixture.Reader.Scans);
        Assert.Equal(1, fixture.Client.Reloads);
    }

    [Fact]
    public async Task ShutdownDuringScheduledScan_CancelsWorkAndNeverRestartsTailing()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        fixture.Reader.BlockNextUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Reader.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Session.BeginShutdown();
        await fixture.Reader.UpdateCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
        Assert.Equal(0, fixture.Reader.Scans);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Fact]
    public async Task ManualRetryBeforeDeadline_CancelsDelayedAttempt()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Delayed", TimeSpan.FromSeconds(30),
            reason: AutomaticReloadReason.Cooldown));
        await fixture.RotateAndWaitAsync();
        await fixture.Session.RetryAutomaticTailingAsync();
        Assert.False(fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        var updates = fixture.Reader.Updates;
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(updates, fixture.Reader.Updates);
        Assert.Equal(1, fixture.Reader.Scans);
    }

    [Fact]
    public async Task DisposeWhileWaiting_CancelsTimerAndDoesNotStartWork()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        var updates = fixture.Reader.Updates;
        fixture.Dispose();
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(updates, fixture.Reader.Updates);
        Assert.DoesNotContain(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
    }

    [Fact]
    public async Task HidingDuringScan_FinishesRecoveryWithoutRestartingHiddenTailing()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reader.BlockNextUpdate = release;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Reader.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Client.Visible = false;
        fixture.Session.SuspendTailingIfNoVisibleClients();
        release.TrySetResult();
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.True(fixture.Session.IsSuspended);
        Assert.DoesNotContain(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
        Assert.Equal(1, fixture.Reader.Scans);
    }

    [Fact]
    public async Task ShutdownDuringNotification_CancelsViewportWork()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Unstable"));
        await fixture.RotateAndWaitAsync();
        fixture.Client.BlockNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Client.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Session.BeginShutdown();
        await fixture.Client.NotificationCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Fact]
    public async Task RealFiles_SimultaneousRolloverRecoversAndContinuesAppendingOnDispatcher()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"weeztail-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            using var paths = AppPaths.BeginTestScope(rootPath: directory);
            var first = Path.Combine(directory, "first.log");
            var second = Path.Combine(directory, "second.log");
            try
            {
                await File.WriteAllTextAsync(first, "old first\n");
                await File.WriteAllTextAsync(second, "old second\n");
                var clock = new RecoveryClock();
                var reader = new ChunkedLogReaderService(ChunkedLogReaderService.GetLastWriteTimeUtc,
                    timestampProvider: () => (long)(clock.GetTimestamp() * (double)Stopwatch.Frequency / clock.TimestampFrequency));
                var tail = new StubFileTailService();
                var detection = new FileEncodingDetectionService();
                var registry = new FileSessionRegistry(reader, tail, detection, TestUiDispatcher.Current, clock)
                { WarmRetentionDuration = TimeSpan.Zero };
                using var tab1 = new LogTabViewModel("one", first, reader, tail, detection, new AppSettings(), false,
                    registry, FileEncoding.Utf8, null, TestUiDispatcher.Current);
                using var tab2 = new LogTabViewModel("two", second, reader, tail, detection, new AppSettings(), false,
                    registry, FileEncoding.Utf8, null, TestUiDispatcher.Current);
                await tab1.LoadAsync();
                await tab2.LoadAsync();
                Assert.False(tab1.HasLoadError, tab1.ActiveSession.LastErrorMessage);
                Assert.False(tab2.HasLoadError, tab2.ActiveSession.LastErrorMessage);
                File.Move(first, first + ".old");
                await File.WriteAllTextAsync(first, "new first\n");
                tail.RaiseFileRotated(first);
                await WaitAsync(() => tab1.VisibleLines.LastOrDefault()?.Text == "new first",
                    () => $"First: {tab1.DisplayStatusText}; {tab1.AutomaticReloadFailureDetail}; lines={tab1.TotalLines}; visible={string.Join(",", tab1.VisibleLines.Select(l => l.Text))}");
                File.Move(second, second + ".old");
                await File.WriteAllTextAsync(second, "new second\n");
                tail.RaiseFileRotated(second);
                await WaitAsync(() => tab2.IsAutomaticReloadPaused && clock.ActiveTimers == 1);
                Assert.Equal("old second", tab2.VisibleLines.Single().Text);
                tab2.StatusText = "unrelated viewport status";
                Assert.Contains("cooldown", tab2.DisplayStatusText);
                clock.Advance(TimeSpan.FromSeconds(30));
                await WaitAsync(() => !tab2.IsAutomaticReloadPaused && tab2.VisibleLines.LastOrDefault()?.Text == "new second");
                await File.AppendAllTextAsync(second, "continued append\n");
                tail.RaiseLinesAppended(second);
                await WaitAsync(() => tab2.VisibleLines.LastOrDefault()?.Text == "continued append");
                Assert.Contains(second, tail.ActiveFiles);
                Assert.Equal(2, tab2.TotalLines);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMonitor_QuietFileCatchesUpAfterTemporaryIdentityLoss(bool rollover)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"weeztail-quiet-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            using var paths = AppPaths.BeginTestScope(rootPath: directory);
            var path = Path.Combine(directory, "quiet.log");
            try
            {
                await File.WriteAllTextAsync(path, "old line\n");
                var identityAvailable = 1;
                var appendAtAdmission = 0;
                var clock = new RecoveryClock();
                var reader = new ChunkedLogReaderService(ChunkedLogReaderService.GetLastWriteTimeUtc,
                    stream => Volatile.Read(ref identityAvailable) == 1
                        ? FileGenerationTokenProvider.Capture(stream) : FileGenerationToken.Unknown,
                    timestampProvider: () =>
                    {
                        // Admission happens after the scan snapshot has been captured.
                        if (Interlocked.Exchange(ref appendAtAdmission, 0) == 1)
                            File.AppendAllText(path, "late append\n");
                        return (long)(clock.GetTimestamp() * (double)Stopwatch.Frequency / clock.TimestampFrequency);
                    });
                using var tail = new FileTailService();
                var detection = new FileEncodingDetectionService();
                var registry = new FileSessionRegistry(reader, tail, detection, TestUiDispatcher.Current, clock)
                { WarmRetentionDuration = TimeSpan.Zero };
                using var tab = new LogTabViewModel("quiet", path, reader, tail, detection, new AppSettings(), false,
                    registry, FileEncoding.Utf8, null, TestUiDispatcher.Current);
                await tab.LoadAsync();
                Assert.False(tab.HasLoadError, tab.ActiveSession.LastErrorMessage);
                Assert.True(tab.ActiveSession.CurrentGenerationToken.IsKnown);
                Volatile.Write(ref identityAvailable, 0);
                if (rollover)
                {
                    File.Move(path, path + ".old");
                    await File.WriteAllTextAsync(path, "replacement line\n");
                }
                else
                    await File.AppendAllTextAsync(path, "quiet append\n");
                await WaitAsync(() => tab.IsAutomaticReloadPaused && clock.ActiveTimers == 1);
                Assert.Equal(1, tab.TotalLines);
                Volatile.Write(ref identityAvailable, 1);
                if (rollover)
                    Volatile.Write(ref appendAtAdmission, 1);
                clock.Advance(TimeSpan.FromSeconds(2));
                await WaitAsync(() => !tab.IsAutomaticReloadPaused && tab.TotalLines == 2 && tab.VisibleLines.Count == 2,
                    () => $"{tab.DisplayStatusText}; {tab.AutomaticReloadFailureDetail}");
                Assert.Equal(rollover ? new[] { "replacement line", "late append" } : new[] { "old line", "quiet append" },
                    tab.VisibleLines.Select(line => line.Text));
                Assert.Equal(rollover ? 1 : 0, tab.SearchContentVersion);
                Assert.False(tab.IsSuspended);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public async Task VisibleResumeIo_SchedulesNormalCatchUpWithoutReplacementHint()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Client.Visible = false;
        fixture.Session.SuspendTailingIfNoVisibleClients();
        fixture.Reader.LineCount = 2;
        fixture.Reader.Failures.Enqueue(new UnauthorizedAccessException("network share temporarily unavailable"));
        fixture.Client.Visible = true;
        fixture.Session.ResumeTailing();
        await WaitAsync(() => fixture.Session.IsAutomaticReloadPaused && fixture.Clock.ActiveTimers == 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal((1, 2), fixture.Client.Advances.Single());
        Assert.Equal(FileChangeHint.None, fixture.Reader.LastHint);
        Assert.Equal(0, fixture.Reader.Scans);
    }

    [Fact]
    public async Task NewRolloverDuringFinalCatchUp_PreservesHintAndPendingReloadThroughCooldown()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new IOException("temporarily absent"));
        await fixture.RotateAndWaitAsync();
        var finalUpdate = fixture.Reader.Updates + 2;
        fixture.Reader.BeforeUpdate = call =>
        {
            if (call != finalUpdate) return;
            fixture.Reader.Generation++;
            fixture.Tail.RaiseFileRotated(fixture.Session.FilePath);
            fixture.Reader.Failures.Enqueue(new AutomaticReloadBlockedException("Delayed", TimeSpan.FromSeconds(30),
                reason: AutomaticReloadReason.Cooldown));
        };
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => fixture.Clock.NextDelay == TimeSpan.FromSeconds(30));
        Assert.Equal(1, fixture.Reader.Scans);
        Assert.Equal(0, fixture.Client.Reloads);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(FileChangeHint.UnspecifiedReplacement, fixture.Reader.LastHint);
        Assert.Equal(2, fixture.Reader.Scans);
        Assert.Equal(1, fixture.Client.Reloads);
        Assert.Equal(fixture.Reader.Generation, fixture.Session.CurrentGenerationToken.FileId);
    }

    [Fact]
    public async Task FinalCatchUpIo_RetainsCommittedReloadWithoutAnotherFullScan()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new IOException("temporarily absent"));
        await fixture.RotateAndWaitAsync();
        var finalUpdate = fixture.Reader.Updates + 2;
        fixture.Reader.BeforeUpdate = call =>
        {
            if (call == finalUpdate)
                fixture.Reader.Failures.Enqueue(new IOException("final catch-up unavailable"));
        };
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => fixture.Clock.NextDelay == TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
        Assert.Equal(1, fixture.Reader.Scans);
        Assert.Equal(0, fixture.Client.Reloads);
        fixture.Reader.LineCount = 2;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused);
        Assert.Equal(1, fixture.Reader.Scans);
        Assert.Equal(1, fixture.Client.Reloads);
        Assert.Equal(2, fixture.Session.TotalLines);
        Assert.Equal(FileChangeHint.None, fixture.Reader.LastHint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventsDuringRecoveryPublication_AreDrainedAfterSuccess(bool replacement)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Failures.Enqueue(new IOException("temporarily absent"));
        await fixture.RotateAndWaitAsync();
        fixture.Client.BlockNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Client.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Session.IsAutomaticReloadPaused);
        Assert.Contains(fixture.Session.FilePath, fixture.Tail.ActiveFiles);
        if (replacement)
        {
            fixture.Reader.Generation++;
            fixture.Tail.RaiseFileRotated(fixture.Session.FilePath);
        }
        else
        {
            fixture.Reader.LineCount = 2;
            fixture.Tail.RaiseLinesAppended(fixture.Session.FilePath);
        }
        fixture.Client.BlockNotification.TrySetResult();
        await WaitAsync(() => !fixture.Session.IsAutomaticReloadPaused &&
            (replacement ? fixture.Client.Reloads == 2 : fixture.Client.Advances.Count == 1));
        Assert.Equal(replacement ? 2 : 1, fixture.Reader.Scans);
        Assert.Equal(replacement ? FileChangeHint.UnspecifiedReplacement : FileChangeHint.None, fixture.Reader.LastHint);
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"Recovery condition did not complete. {describe?.Invoke()}");
            await Task.Delay(10);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public RecoveryClock Clock { get; } = new();
        public Reader Reader { get; } = new();
        public StubFileTailService Tail { get; } = new();
        public Client Client { get; } = new();
        public FileSession Session { get; }
        private Fixture()
        {
            Session = new(new FileSessionKey(@"C:\test\recovery.log", FileEncoding.Utf8), Reader, Tail,
                new FileEncodingDetectionService(), TestUiDispatcher.Current, Clock);
            Session.AttachClient(Client);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Session.LoadAsync();
            return fixture;
        }
        public async Task RotateAndWaitAsync()
        {
            Reader.Generation++;
            Tail.RaiseFileRotated(Session.FilePath);
            await WaitAsync(() => Session.IsAutomaticReloadPaused && Clock.ActiveTimers == 1);
        }
        public void Dispose() => Session.Dispose();
    }

    private sealed class Client : IFileSessionClient
    {
        public bool Visible { get; set; } = true;
        public bool IsSessionClientDisposed => false;
        public bool IsSessionClientVisible => Visible;
        public ConcurrentQueue<Exception> Failures { get; } = new();
        public int Reloads { get; private set; }
        public ConcurrentQueue<(int Previous, int Updated)> Advances { get; } = new();
        public TaskCompletionSource? BlockNotification;
        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NotificationCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task HandleSessionReloadedAsync(CancellationToken ct)
        {
            if (Failures.TryDequeue(out var failure)) throw failure;
            if (BlockNotification is { } blocked)
            {
                NotificationStarted.TrySetResult();
                try { await blocked.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { NotificationCanceled.TrySetResult(); throw; }
            }
            Reloads++;
        }
        public Task HandleSessionContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
        {
            if (Failures.TryDequeue(out var failure)) throw failure;
            Advances.Enqueue((previousTotalLines, updatedLineCount));
            return Task.CompletedTask;
        }
        public void SetStatusText(string statusText) { }
    }

    private sealed class Reader : ILogReaderService
    {
        public ConcurrentQueue<Exception> Failures { get; } = new();
        public ulong Generation { get; set; } = 1;
        public int LineCount { get; set; } = 1;
        public int Updates;
        public Action<int>? BeforeUpdate;
        public int Scans;
        public long LastAdmissionTimestamp;
        public FileChangeHint LastHint;
        public TaskCompletionSource? BlockNextUpdate;
        public TaskCompletionSource UpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpdateCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LineIndex CreateIndex(string path)
        {
            var index = new LineIndex { FilePath = path, FileSize = 100, GenerationToken = FileGenerationToken.Create(1, Generation) };
            for (var line = 0; line < LineCount; line++) index.LineOffsets.Add(line * 10);
            return index;
        }
        public Task<LineIndex> BuildIndexAsync(string path, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(path));
        public Task<LineIndex> UpdateIndexAsync(string path, LineIndex index, FileEncoding encoding, CancellationToken ct = default)
            => UpdateIndexAsync(path, index, encoding, FileChangeHint.None, ct);
        public async Task<LineIndex> UpdateIndexAsync(string path, LineIndex index, FileEncoding encoding, FileChangeHint hint, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref Updates);
            BeforeUpdate?.Invoke(call);
            LastHint = hint;
            LastAdmissionTimestamp = index.AutomaticReloadNotBeforeTimestamp;
            if (Failures.TryDequeue(out var failure)) throw failure;
            if (BlockNextUpdate is { } blocked)
            {
                UpdateStarted.TrySetResult();
                try { await blocked.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { UpdateCanceled.TrySetResult(); throw; }
                BlockNextUpdate = null;
            }
            if (index.GenerationToken.FileId == Generation)
            {
                while (index.LineCount < LineCount) index.LineOffsets.Add(index.LineCount * 10);
                return index;
            }
            Interlocked.Increment(ref Scans);
            var replacement = CreateIndex(path);
            replacement.ReplacesPriorGeneration = true;
            replacement.AutomaticReloadNotBeforeTimestamp = index.AutomaticReloadNotBeforeTimestamp;
            return replacement;
        }
        public Task<IReadOnlyList<string>> ReadLinesAsync(string path, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["line"]);
        public Task<string> ReadLineAsync(string path, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult("line");
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<RecoveryTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public int ActiveTimers { get { lock (_gate) return _timers.Count(t => !t.Disposed && t.Due != long.MaxValue); } }
        public TimeSpan NextDelay
        {
            get { lock (_gate) return TimeSpan.FromTicks(_timers.Where(t => !t.Disposed && t.Due != long.MaxValue).Select(t => t.Due).DefaultIfEmpty(long.MaxValue).Min() - _timestamp); }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new RecoveryTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan amount)
        {
            RecoveryTimer[] ready;
            lock (_gate)
            {
                _timestamp += amount.Ticks;
                ready = _timers.Where(t => !t.Disposed && t.Due <= _timestamp).ToArray();
                foreach (var timer in ready) timer.Due = long.MaxValue;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class RecoveryTimer(RecoveryClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (Disposed) return false;
                    if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Recovery tests use one-shot timers.");
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._timestamp + dueTime.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
