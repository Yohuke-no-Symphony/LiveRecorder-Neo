using System.Text.Json;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies per-broadcast observations preserve durable counters and fairly report a bounded backlog.</summary>
public sealed class RecordingSessionProgressTests
{
    /// <summary>In-flight transfer bytes never advance durable acknowledgements or complete a still-growing broadcast.</summary>
    [Fact]
    public void TransferProgressRemainsSeparateFromAcknowledgedArchiveBytes()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        RecordingSpoolFiles.Write(Path.Combine(session.DirectoryPath, "录制提交", "segment", "latest.json"),
            new Yohuke.LiveRecorder.Application.Media.MediaRecordingCommitSnapshot
            {
                SegmentId = "segment", MediaCommittedBytes = 100, IndexCommittedBytes = 10, DurationMs = 1000
            });
        spool.UpdateSession(session.DirectoryPath, value => value.Events = new() { CommittedBytes = 30, IndexCommittedBytes = 5 });
        RecordingSpoolFiles.Write(Path.Combine(session.DirectoryPath, "uploaded.json"), new RecordingUploadReceipt
        {
            Segments = new() { ["segment"] = new() { MediaCommittedBytes = 80, IndexCommittedBytes = 8 } },
            Events = new() { CommittedBytes = 20, IndexCommittedBytes = 3 }
        });
        var reporter = new RecordingSessionProgressReporter(spool);
        reporter.Transfer("session", new() { ArtifactKind = "media", BytesTransferred = 99, TotalBytes = 100 });

        var snapshot = Assert.Single(reporter.Batch([new() { SessionId = "session", State = "Recording" }]));

        Assert.Equal(145, snapshot.CommittedArchiveBytes);
        Assert.Equal(111, snapshot.AcknowledgedArchiveBytes);
        Assert.Equal(1000, snapshot.CapturedDurationMs);
        Assert.Equal("Recording", snapshot.CaptureState);
        Assert.Equal("Uploading", snapshot.UploadState);
        Assert.Equal(99, snapshot.CurrentTransfer!.BytesTransferred);
        Assert.False(snapshot.Descriptor.IsClosed);
    }

    /// <summary>Active capture stays visible while older queued broadcasts rotate rather than being silently truncated.</summary>
    [Fact]
    public void BoundedBatchesRotateTheEntireBacklog()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var count = RecordingSessionProgress.MAX_BATCH_COUNT * 2 + 7;
        for (var index = 0; index < count; index++)
        {
            spool.CreateSession(fixture.Assignment, new()
            {
                SessionId = $"session-{index:D4}", RoomId = fixture.Assignment.RoomId, StartedAtUtc = DateTime.UtcNow
            }, fixture.DirectoryPath);
        }
        var reporter = new RecordingSessionProgressReporter(spool);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 4; index++)
        {
            var batch = reporter.Batch([new() { SessionId = "session-0000", State = "Recording" }]);
            Assert.InRange(batch.Count, 1, RecordingSessionProgress.MAX_BATCH_COUNT);
            Assert.Contains(batch, value => value.Descriptor.SessionId == "session-0000" && value.CaptureState == "Recording");
            seen.UnionWith(batch.Select(value => value.Descriptor.SessionId));
        }
        Assert.Equal(count, seen.Count);
    }

    /// <summary>Long localized metadata cannot let recording observations exhaust the shared control frame.</summary>
    [Fact]
    public void BatchesRespectTheirSerializedByteBudget()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        for (var index = 0; index < 30; index++)
        {
            spool.CreateSession(fixture.Assignment, new()
            {
                SessionId = $"session-{index:D4}", RoomId = fixture.Assignment.RoomId, StartedAtUtc = DateTime.UtcNow,
                Title = new('录', 1024), StreamerName = new('播', 256), CoverUrl = "https://example.invalid/" + new string('a', 4000)
            }, fixture.DirectoryPath);
        }
        var reporter = new RecordingSessionProgressReporter(spool);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 10; index++)
        {
            var batch = reporter.Batch([]);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(batch, WorkerProtocol.JsonOptions).Length, 1,
                RecordingSessionProgressReporter.MAX_BATCH_BYTES);
            seen.UnionWith(batch.Select(value => value.Descriptor.SessionId));
        }
        Assert.Equal(30, seen.Count);
    }

    /// <summary>A quarantined broadcast without committed media remains a visible skipped task after restart.</summary>
    [Fact]
    public void NoMediaDraftIsReportedAfterSpoolRestart()
    {
        using var fixture = new RecordingWorkerFixture();
        using (var spool = fixture.Spool())
        {
            spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
            var session = fixture.Session(spool);
            spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
            spool.QuarantineDraft(session.DirectoryPath);
        }
        using var restarted = fixture.Spool();
        var snapshot = Assert.Single(new RecordingSessionProgressReporter(restarted).Batch([]));

        Assert.Equal("NoMedia", snapshot.CaptureState);
        Assert.Equal("Skipped", snapshot.UploadState);
        Assert.True(snapshot.Descriptor.IsClosed);
        Assert.Equal(0, snapshot.CommittedArchiveBytes);
        Assert.Empty(restarted.Sessions());
    }

    /// <summary>Transient upload failure is visible without changing durable media or inventing a final acknowledgement.</summary>
    [Fact]
    public void UploadFailureRetainsQueuedSessionIdentity()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        fixture.Session(spool);
        var reporter = new RecordingSessionProgressReporter(spool);
        reporter.Failed("session");

        var snapshot = Assert.Single(reporter.Batch([]));

        Assert.Equal("Retrying", snapshot.UploadState);
        Assert.Equal("RecordingUploadFailed", snapshot.ErrorCode);
        Assert.Equal(fixture.Assignment.AssignmentId, snapshot.AssignmentId);
        Assert.Single(spool.Sessions());
    }
}
