using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Recording.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies broadcast-scoped logs around durable recording upload acknowledgements.</summary>
public sealed class RecordingUploadLogTests
{
    /// <summary>Actual media, events and final archive acknowledgements retain one broadcast identity through cleanup.</summary>
    [Fact]
    public async Task UploadAcknowledgementsAreLoggedAgainstTheirSessionAndStage()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        await WriteMediaAsync(session.DirectoryPath, true);
        await using (var writer = new SessionEventWriter(Path.Combine(session.DirectoryPath, "弹幕"),
                         commit => spool.RecordEventsAsync(session.DirectoryPath, commit)))
        {
            await writer.WriteAsync(new RecordedEventEnvelope { Text = "durable event", OffsetMs = 1 });
        }
        spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
        using var handler = new RecordingUploadLogTestHandler();
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var uploader = new RecordingSpoolUploader(spool, transfer, () => fixture.Configuration, _ => false, entry =>
        {
            entries.Enqueue(entry);
            if (entry.Message.Contains("complete broadcast archive", StringComparison.Ordinal))
            {
                completed.TrySetResult();
            }
        });
        var upload = uploader.RunAsync(cancellation.Token);
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(Directory.Exists(session.DirectoryPath));
            Assert.Equal(1, handler.SegmentCommits);
            Assert.Equal(1, handler.EventCommits);
            Assert.Equal(2, handler.Registrations);
            Assert.Contains(entries, value => value.Message.Contains("acknowledged media segment", StringComparison.Ordinal));
            Assert.Contains(entries, value => value.Message.Contains("acknowledged event sequence", StringComparison.Ordinal));
            Assert.All(entries, AssertSessionLog);
            Assert.All(entries, value => Assert.Equal("Information", value.Level));
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    /// <summary>A broadcast retains its local media until the entire session closes, including already closed segments.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BroadcastUploadsOnlyAfterItsSessionCloses(bool segmentClosed)
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        await WriteMediaAsync(session.DirectoryPath, segmentClosed);
        using var handler = new RecordingUploadLogTestHandler();
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        using var cancellation = new CancellationTokenSource();
        var uploader = new RecordingSpoolUploader(spool, transfer, () => fixture.Configuration, _ => false, entries.Enqueue);
        var upload = uploader.RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(1100);

            Assert.Equal(0, handler.Registrations);
            Assert.Equal(0, handler.SegmentCommits);
            Assert.Equal(0, handler.EventCommits);
            Assert.Empty(entries);
            Assert.True(File.Exists(Path.Combine(session.DirectoryPath, "video.flv")));
            Assert.False(File.Exists(Path.Combine(session.DirectoryPath, "uploaded.json")));

            await WriteMediaAsync(session.DirectoryPath, true);
            spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
            await WaitAsync(() => !Directory.Exists(session.DirectoryPath));

            Assert.Equal(2, handler.Registrations);
            Assert.Equal(1, handler.SegmentCommits);
            Assert.Equal(1, handler.EventCommits);
            Assert.All(entries, AssertSessionLog);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    /// <summary>A completed broadcast drains while another broadcast continues capturing into its own local files.</summary>
    [Fact]
    public async Task ClosedBroadcastUploadsWhileAnotherBroadcastRemainsOpen()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var active = spool.CreateSession(fixture.Assignment, new()
        {
            SessionId = "active-session", RoomId = fixture.Assignment.RoomId, StartedAtUtc = DateTime.UtcNow
        }, fixture.DirectoryPath);
        await WriteMediaAsync(active.DirectoryPath, true);
        var closed = fixture.Session(spool);
        await WriteMediaAsync(closed.DirectoryPath, true);
        spool.CloseSession(closed.DirectoryPath, DateTime.UtcNow);
        using var handler = new RecordingUploadLogTestHandler();
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        var configuration = fixture.Configuration;
        using var cancellation = new CancellationTokenSource();
        var upload = new RecordingSpoolUploader(spool, transfer, () => configuration, _ => false, entries.Enqueue)
            .RunAsync(cancellation.Token);
        try
        {
            await WaitAsync(() => !Directory.Exists(closed.DirectoryPath));
            await Task.Delay(1100);

            Assert.Equal(2, handler.Registrations);
            Assert.Equal(1, handler.SegmentCommits);
            Assert.Equal(1, handler.EventCommits);
            Assert.True(File.Exists(Path.Combine(active.DirectoryPath, "video.flv")));
            Assert.False(File.Exists(Path.Combine(active.DirectoryPath, "uploaded.json")));
            Assert.All(entries, AssertSessionLog);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    /// <summary>Recovery seals an interrupted segment while its broadcast still waits for the session's final closure.</summary>
    [Fact]
    public async Task RecoveredBroadcastWaitsForFinalClosureBeforeUploading()
    {
        using var fixture = new RecordingWorkerFixture();
        string directory;
        using (var spool = fixture.Spool())
        {
            spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
            directory = fixture.Session(spool).DirectoryPath;
            await WriteMediaAsync(directory, false);
        }
        using var recovered = fixture.Spool();
        await recovered.RecoverAsync(CancellationToken.None);
        Assert.True(Assert.Single(recovered.Commits(directory)).IsClosed);
        Assert.False(Assert.Single(recovered.Sessions()).State.Descriptor.IsClosed);
        using var handler = new RecordingUploadLogTestHandler();
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        var configuration = fixture.Configuration;
        using var cancellation = new CancellationTokenSource();
        var upload = new RecordingSpoolUploader(recovered, transfer, () => configuration, _ => false, entries.Enqueue)
            .RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(1100);

            Assert.Equal(0, handler.Registrations);
            Assert.True(File.Exists(Path.Combine(directory, "video.flv")));

            recovered.CloseSession(directory, DateTime.UtcNow);
            await WaitAsync(() => !Directory.Exists(directory));

            Assert.Equal(1, handler.SegmentCommits);
            Assert.Equal(1, handler.EventCommits);
            Assert.All(entries, AssertSessionLog);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    /// <summary>Closed sessions without committed video remain local drafts through the deferred upload scan.</summary>
    [Fact]
    public async Task ClosedEmptyBroadcastIsRetainedAsADraft()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
        using var handler = new RecordingUploadLogTestHandler();
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var configuration = fixture.Configuration;
        using var cancellation = new CancellationTokenSource();
        var upload = new RecordingSpoolUploader(spool, transfer, () => configuration, _ => false, _ => { })
            .RunAsync(cancellation.Token);
        try
        {
            await WaitAsync(() => spool.Sessions().Count == 0);

            var draft = Assert.Single(spool.Sessions(includeDrafts: true));
            Assert.Equal("session", draft.State.Descriptor.SessionId);
            Assert.Equal("drafts", Path.GetFileName(Path.GetDirectoryName(draft.DirectoryPath)));
            Assert.Equal(0, handler.Registrations);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    /// <summary>A rejected session upload retains the spool and reports a scoped warning without assigning node messages to that session.</summary>
    [Fact]
    public async Task UploadFailureKeepsWarningSeverityAndNodeMessagesSeparate()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        await WriteMediaAsync(session.DirectoryPath, true);
        spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
        using var handler = new RecordingUploadLogTestHandler { RejectRegistration = true };
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        using var cancellation = new CancellationTokenSource();
        var uploader = new RecordingSpoolUploader(spool, transfer, () => fixture.Configuration, _ => false, entries.Enqueue);
        var upload = uploader.RunAsync(cancellation.Token);
        try
        {
            await WaitAsync(() => entries.Any(value => value.JobId is null));

            var scoped = Assert.Single(entries, value => value.JobId is not null);
            AssertSessionLog(scoped);
            Assert.Equal("Warning", scoped.Level);
            var node = Assert.Single(entries, value => value.JobId is null);
            Assert.Null(node.Stage);
            Assert.Equal("Warning", node.Level);
            Assert.True(Directory.Exists(session.DirectoryPath));
            Assert.False(File.Exists(Path.Combine(session.DirectoryPath, "uploaded.json")));
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    /// <summary>Rejected media commits retain the server failure details and keep unacknowledged files available for retry.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.InsufficientStorage)]
    public async Task RejectedMediaCommitLogsServerDetailsAndRetainsSpool(HttpStatusCode status)
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        await WriteMediaAsync(session.DirectoryPath, true);
        spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
        using var handler = new RecordingUploadLogTestHandler { RejectedSegmentCommitStatus = status };
        using var transfer = new RecordingFileTransfer(new() { ApiBaseUrl = "https://test.invalid", WorkerId = "worker", Secret = "secret" }, handler);
        var entries = new ConcurrentQueue<WorkerLogEntry>();
        using var cancellation = new CancellationTokenSource();
        var configuration = fixture.Configuration;
        var uploader = new RecordingSpoolUploader(spool, transfer, () => configuration, _ => false, entries.Enqueue);
        var upload = uploader.RunAsync(cancellation.Token);
        try
        {
            await WaitAsync(() => entries.Any(value => value.JobId is null));

            var warning = Assert.Single(entries, value => value is { JobId: "session", Level: "Warning" });
            AssertSessionLog(warning);
            var nodeWarning = Assert.Single(entries, value => value.JobId is null);
            Assert.All(new[] { warning, nodeWarning }, value =>
            {
                Assert.Contains("HttpRequestException", value.Message, StringComparison.Ordinal);
                Assert.Contains("POST /api/worker/recording/targets/assignment/sessions/session/segments/segment/commit", value.Message, StringComparison.Ordinal);
                Assert.Contains(FormattableString.Invariant($"status {(int)status}"), value.Message, StringComparison.Ordinal);
                Assert.Contains("The recording segment commit was rejected.", value.Message, StringComparison.Ordinal);
            });
            Assert.True(Directory.Exists(session.DirectoryPath));
            Assert.True(File.Exists(Path.Combine(session.DirectoryPath, "video.flv")));
            Assert.True(File.Exists(Path.Combine(session.DirectoryPath, "video.index.jsonl")));
            Assert.False(File.Exists(Path.Combine(session.DirectoryPath, "uploaded.json")));
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
    }

    private static void AssertSessionLog(WorkerLogEntry value)
    {
        Assert.Equal("session", value.JobId);
        Assert.Equal("Uploading", value.Stage);
        Assert.Null(value.Attempt);
        Assert.Null(value.Source);
    }

    private static async Task WriteMediaAsync(string directory, bool closed)
    {
        var prefix = new byte[] { 1, 2, 3, 4 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(prefix));
        var blob = Path.Combine(directory, "录制提交", "segment", "prefixes", hash + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        await File.WriteAllBytesAsync(blob, prefix);
        await File.WriteAllBytesAsync(Path.Combine(directory, "video.flv"), new byte[20]);
        await File.WriteAllBytesAsync(Path.Combine(directory, "video.index.jsonl"), new byte[10]);
        RecordingSpoolFiles.Write(Path.Combine(directory, "录制提交", "segment", "latest.json"), new MediaRecordingCommitSnapshot
        {
            SegmentId = "segment", RelativePath = "video.flv", IndexRelativePath = "video.index.jsonl",
            CommitSequence = 7, MediaCommittedBytes = 20, IndexCommittedBytes = 10, PrefixLength = 4,
            PrefixSha256 = hash, PrefixRelativePath = Path.GetRelativePath(directory, blob), DurationMs = 1000, IsClosed = closed
        });
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
