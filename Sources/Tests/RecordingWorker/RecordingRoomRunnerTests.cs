using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Yohuke.LiveRecorder.Recording.Tests;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies persistent capture lifetime independently of the coordinator transport.</summary>
public sealed class RecordingRoomRunnerTests
{
    /// <summary>Shutdown stops capture and seals the current attempt while preserving the open broadcast for restart.</summary>
    [Fact]
    public async Task ShutdownPreservesOpenSessionAndOwnership()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var source = new RecordingTestCaptureSource();
        var recorder = new RecordingTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(fixture.Assignment, spool, source, _ => recorder,
            () => fixture.Configuration, TimeProvider.System, _ => { }, cancellation.Token);
        runner.Start();
        await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var room = runner.Snapshot().Room;
        Assert.NotNull(room);
        Assert.Equal(123, room.RoomId);
        Assert.Equal(456, room.Uid);
        Assert.Equal("Recorder", room.Name);
        Assert.True(room.IsLive);

        await cancellation.CancelAsync();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(recorder.Stopped.Task.IsCompleted);
        Assert.False(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
        Assert.False(Assert.Single(spool.Assignments()).Released);
    }

    /// <summary>An explicit stop closes the broadcast after media and event capture have drained.</summary>
    [Fact]
    public async Task DisabledAssignmentStopsAndClosesBeforeOwnershipRelease()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var recorder = new RecordingTestRecorder();
        var source = new RecordingTestCaptureSource();
        using var runner = new RecordingRoomRunner(fixture.Assignment, spool, source, _ => recorder,
            () => fixture.Configuration, TimeProvider.System, _ => { }, CancellationToken.None);
        runner.Start();
        await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runner.Update(fixture.Assignment with { Enabled = false, ConfigurationRevision = 2 });
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(recorder.Stopped.Task.IsCompleted);
        Assert.True(source.EventsStopped.Task.IsCompleted);
        Assert.True(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
        Assert.False(Assert.Single(spool.Assignments()).Released);
    }

    /// <summary>Account or event settings revisions cancel the current connections without replacing the broadcast session.</summary>
    [Fact]
    public async Task AssignmentRevisionStopsOldCaptureAndEventIdentity()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var recorder = new RecordingTestRecorder();
        var source = new RecordingTestCaptureSource();
        using var runner = new RecordingRoomRunner(fixture.Assignment, spool, source, _ => recorder,
            () => fixture.Configuration, TimeProvider.System, _ => { }, CancellationToken.None);
        runner.Start();
        await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sessionId = Assert.Single(spool.Sessions()).State.Descriptor.SessionId;

        runner.Update(fixture.Assignment with { ConfigurationRevision = 2, RecordDanmu = false });
        await recorder.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.EventsStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(sessionId, Assert.Single(spool.Sessions()).State.Descriptor.SessionId);
        Assert.False(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
        runner.Update(fixture.Assignment with { ConfigurationRevision = 3, Enabled = false });
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Every room has a finite GOP split budget even when the source configuration requests an unlimited file.</summary>
    [Theory]
    [InlineData(0, 8 * 1024 * 1024)]
    [InlineData(4 * 1024 * 1024, 4 * 1024 * 1024)]
    [InlineData(20 * 1024 * 1024, 8 * 1024 * 1024)]
    public void RecordingFilesAreBoundedBySpoolBudget(long requestedBytes, long expectedBytes)
    {
        using var fixture = new RecordingWorkerFixture();

        Assert.Equal(expectedBytes, RecordingSegmentPolicy.MaximumBytes(fixture.Configuration with { MaxConcurrentEncodes = 2 },
            fixture.Assignment with { SegmentBytes = requestedBytes }));
    }

    /// <summary>Reconnecting to a header-only stream cannot extend the original session's media silence deadline.</summary>
    [Fact]
    public async Task ReconnectPreservesTheSessionInactivityDeadline()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var assignment = fixture.Assignment with { RecordDanmu = false, RecordingTimeoutMinutes = 1 };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        var recorder = new RecordingRetryTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => recorder,
            () => fixture.Configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        await recorder.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(40));
        recorder.EndFirst.TrySetResult();
        await WaitAsync(() => runner.Snapshot().State == "Retrying");
        clock.Advance(TimeSpan.FromSeconds(2));
        await recorder.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(18));
        await recorder.SecondStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitAsync(() => Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);

        Assert.True(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
        Assert.False(Assert.Single(spool.Assignments()).Released);
        await cancellation.CancelAsync();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A stop persisted before a process crash drains the recovered broadcast without resuming capture.</summary>
    [Fact]
    public async Task PersistedDisabledAssignmentClosesItsRecoveredOpenSession()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var assignment = fixture.Assignment with { Enabled = false, ConfigurationRevision = 2 };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        fixture.Session(spool);
        var recorder = new RecordingTestRecorder();
        using var runner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => recorder,
            () => fixture.Configuration, TimeProvider.System, _ => { }, CancellationToken.None);

        runner.Start();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(recorder.Started.Task.IsCompleted);
        Assert.True(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
        Assert.True(Assert.Single(spool.Sessions()).State.Events!.IsClosed);
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
