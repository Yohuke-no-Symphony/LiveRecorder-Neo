using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Recording.Tests;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies cancellation callbacks can inspect or report room progress without waiting for a held room lock.</summary>
public sealed class RecordingCancellationTests
{
    /// <summary>A configuration stop observes media cancellation callbacks that inspect the runner and report final progress.</summary>
    [Fact]
    public async Task UpdateDoesNotHoldTheRoomLockWhileMediaCallbacksRun()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        RecordingRoomRunner? runner = null;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new RecordingTestRecorder(progress =>
        {
            var inspect = Task.Run(() =>
            {
                runner!.Snapshot();
                progress.Report(new MediaRecordingUpdate { Kind = "Closed", SegmentId = "segment", CommittedBytes = 1000, DurationMs = 1000 });
            });
            completed.TrySetResult(inspect.Wait(TimeSpan.FromSeconds(2)));
        });
        using var cancellation = new CancellationTokenSource();
        using var active = new RecordingRoomRunner(fixture.Assignment, spool, new RecordingTestCaptureSource(), _ => recorder,
            () => fixture.Configuration, TimeProvider.System, _ => { }, cancellation.Token);
        runner = active;
        active.Start();
        await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(() => active.Update(fixture.Assignment with { ConfigurationRevision = 2, Enabled = false }))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await cancellation.CancelAsync();
            await active.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Offline event shutdown and a concurrent revision share one cancellation lifetime until the callback finishes.</summary>
    [Fact]
    public async Task StopEventsAndUpdateCanCancelTheSameSubscriptionWithoutHoldingTheRoomLock()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        RecordingRoomRunner? runner = null;
        var inspected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishCallback = new ManualResetEventSlim();
        var source = new RecordingTestCaptureSource(() =>
        {
            var inspect = Task.Run(() => runner!.Snapshot());
            inspected.TrySetResult(inspect.Wait(TimeSpan.FromSeconds(2)));
            finishCallback.Wait(TimeSpan.FromSeconds(5));
        });
        var clock = new ManualRecordingTimeProvider();
        var recorder = new RecordingTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var active = new RecordingRoomRunner(fixture.Assignment, spool, source, _ => recorder,
            () => fixture.Configuration, clock, _ => { }, cancellation.Token);
        runner = active;
        active.Start();
        await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            source.IsLive = false;
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitAsync(() => active.Snapshot().State == "Retrying");
            clock.Advance(TimeSpan.FromSeconds(2));
            var callbackCanInspect = await inspected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.Run(() => active.Update(fixture.Assignment with { ConfigurationRevision = 2, RecordDanmu = false }))
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(callbackCanInspect);
            Assert.False(active.Completion.IsCompleted);
        }
        finally
        {
            finishCallback.Set();
            await cancellation.CancelAsync();
            await active.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Callback failures are observed by the room task while an explicit stop still seals the durable session.</summary>
    [Fact]
    public async Task RoomCompletionObservesEventCancellationFailures()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var source = new RecordingTestCaptureSource(() => throw new InvalidOperationException("Controlled cancellation failure."));
        var recorder = new RecordingTestRecorder();
        using var active = new RecordingRoomRunner(fixture.Assignment, spool, source, _ => recorder,
            () => fixture.Configuration, TimeProvider.System, _ => { }, CancellationToken.None);
        active.Start();
        await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        active.Update(fixture.Assignment with { ConfigurationRevision = 2, Enabled = false });
        var error = await Record.ExceptionAsync(() => active.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Contains("Controlled cancellation failure.", error.ToString());
        Assert.True(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
        Assert.False(Assert.Single(spool.Assignments()).Released);
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
