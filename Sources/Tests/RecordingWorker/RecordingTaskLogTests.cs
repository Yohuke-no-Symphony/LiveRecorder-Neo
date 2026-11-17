using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies explicit broadcast identity and severity for remote capture logs.</summary>
public sealed class RecordingTaskLogTests
{
    /// <summary>Capture logs retain their broadcast identity through the existing heartbeat buffer and process shutdown.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureLogsFollowTheNewOrRecoveredSessionThroughShutdown(bool recovered)
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var previous = recovered ? fixture.Session(spool) : null;
        if (previous is not null)
        {
            spool.UpdateSession(previous.DirectoryPath, value => value.LastCapturedAtUtc = value.Descriptor.StartedAtUtc);
        }
        var buffer = new WorkerLogBuffer();
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        var source = new RecordingTestCaptureSource();
        var recorder = new RecordingTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(fixture.Assignment, spool, source, _ => recorder,
            () => fixture.Configuration, TimeProvider.System, entry =>
            {
                entries.Enqueue(entry);
                buffer.Append(entry);
            }, cancellation.Token);
        runner.Start();
        await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitAsync(() => entries.Any(value => value.Message.Contains(recovered ? "Media capture resumed" : "first committed media",
            StringComparison.Ordinal)));

        await cancellation.CancelAsync();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(fixture.Assignment.AssignmentId, request.SessionId);
        if (previous is not null)
        {
            Assert.Equal(previous.State.Descriptor.SessionId, request.SessionId);
        }
        var batch = Assert.IsType<WorkerLogBatch>(buffer.GetBatch());
        Assert.Same(batch, buffer.GetBatch());
        Assert.All(batch.Entries, value =>
        {
            Assert.Equal(request.SessionId, value.JobId);
            Assert.Equal("Capturing", value.Stage);
            Assert.Equal("Information", value.Level);
            Assert.Null(value.Attempt);
            Assert.Null(value.Source);
            Assert.True(value.Sequence > 0);
        });
        Assert.Single(batch.Entries, value => value.Message.Contains(recovered ? "Media capture resumed" : "first committed media",
            StringComparison.Ordinal));
        Assert.Contains(batch.Entries, value => value.Message.Contains(recovered ? "Recovered" : "Created", StringComparison.Ordinal));
        Assert.Contains(batch.Entries, value => value.Message.Contains("paused for worker shutdown", StringComparison.Ordinal));
        Assert.False(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
    }

    /// <summary>Warnings attach to a broadcast only after that broadcast exists, including independent event reconnections.</summary>
    [Theory]
    [InlineData("Room", false)]
    [InlineData("Stream", true)]
    [InlineData("Events", true)]
    public async Task CaptureFailuresKeepTheirSeverityAndExplicitSessionScope(string failure, bool hasSession)
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        var warning = new TaskCompletionSource<WorkerLogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(fixture.Assignment, spool, new RecordingLogFailureSource(failure),
            _ => new RecordingTestRecorder(), () => fixture.Configuration, TimeProvider.System, entry =>
            {
                entries.Enqueue(entry);
                if (entry.Level == "Warning")
                {
                    warning.TrySetResult(entry);
                }
            }, cancellation.Token);
        runner.Start();
        var observed = await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Warning", observed.Level);
        Assert.Null(observed.Attempt);
        Assert.Null(observed.Source);
        if (hasSession)
        {
            var sessionId = Assert.Single(spool.Sessions()).State.Descriptor.SessionId;
            Assert.Equal(sessionId, observed.JobId);
            Assert.Equal("Capturing", observed.Stage);
            Assert.All(entries, value => Assert.Equal(sessionId, value.JobId));
        }
        else
        {
            Assert.Null(observed.JobId);
            Assert.Null(observed.Stage);
            Assert.Empty(spool.Sessions());
        }
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
