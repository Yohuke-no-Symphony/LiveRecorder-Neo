using System.Security.Cryptography;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies durable capture ownership and recovery without deleting unacknowledged archives.</summary>
public sealed class RecordingSpoolTests
{
    /// <summary>版本化活动状态不会被时钟回拨前较大的历史 UTC 提交时间覆盖。</summary>
    [Fact]
    public async Task RecoveryKeepsVersionedActivityAcrossClockEpochs()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        var received = DateTime.UtcNow;
        spool.UpdateSession(session.DirectoryPath, value =>
        {
            value.LastMediaReceivedAtUtc = received;
            value.Descriptor = value.Descriptor with
            {
                Lifecycle = new() { Revision = 4, LastMediaReceivedAtUtc = received }
            };
        });
        RecordingSpoolFiles.Write(Path.Combine(session.DirectoryPath, "录制提交", "older-clock", "latest.json"),
            new MediaRecordingCommitSnapshot
            {
                SegmentId = "older-clock", IsClosed = true, LastMediaReceivedAtUtc = received.AddHours(1)
            });

        await spool.RecoverAsync(CancellationToken.None);

        Assert.Equal(received, Assert.Single(spool.Sessions()).State.LastMediaReceivedAtUtc);
    }

    /// <summary>Different configuration files cannot run two writers against the same physical Worker spool.</summary>
    [Fact]
    public void SpoolOwnershipIsExclusiveAcrossSettingsFiles()
    {
        using var fixture = new RecordingWorkerFixture();
        using (var spool = fixture.Spool())
        {
            Assert.Throws<IOException>(() => new RecordingSpool(Path.Combine(fixture.DirectoryPath, "other-settings.json"),
                "worker", fixture.DirectoryPath));
        }
        using var recovered = fixture.Spool();
        Assert.Empty(recovered.Sessions());
    }

    /// <summary>Cached room credentials survive restart and are private to the owner on Unix.</summary>
    [Fact]
    public void AssignmentPersistsCredentialsPrivatelyAcrossRestart()
    {
        using var fixture = new RecordingWorkerFixture();
        using (var spool = fixture.Spool())
        {
            spool.SaveAssignment(fixture.Assignment with { Accounts = [new() { AccountId = "account", Cookie = "private-cookie" }] },
                fixture.DirectoryPath);
            var path = Directory.EnumerateFiles(fixture.DirectoryPath, "assignment.json", SearchOption.AllDirectories).Single();
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        using var restarted = fixture.Spool();
        Assert.Equal("private-cookie", Assert.Single(Assert.Single(restarted.Assignments()).Assignment.Accounts).Cookie);
    }

    /// <summary>A process restart restores the committed prefix and truncates uncommitted media and index tails.</summary>
    [Fact]
    public async Task RecoversOnlyDurableMediaAndIndexBytes()
    {
        using var fixture = new RecordingWorkerFixture();
        string directory;
        using (var spool = fixture.Spool())
        {
            spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
            directory = fixture.Session(spool).DirectoryPath;
            var prefix = new byte[] { 1, 2, 3, 4 };
            var hash = Convert.ToHexStringLower(SHA256.HashData(prefix));
            var blob = Path.Combine(directory, "录制提交", "segment", "prefixes", hash + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
            await File.WriteAllBytesAsync(blob, prefix);
            await File.WriteAllBytesAsync(Path.Combine(directory, "video.flv"), new byte[30]);
            await File.WriteAllBytesAsync(Path.Combine(directory, "video.index.jsonl"), new byte[25]);
            RecordingSpoolFiles.Write(Path.Combine(directory, "录制提交", "segment", "latest.json"), new MediaRecordingCommitSnapshot
            {
                SegmentId = "segment", RelativePath = "video.flv", IndexRelativePath = "video.index.jsonl",
                CommitSequence = 7, MediaCommittedBytes = 20, IndexCommittedBytes = 10, PrefixLength = 4,
                PrefixSha256 = hash, PrefixRelativePath = Path.GetRelativePath(directory, blob), DurationMs = 1000
            });
        }
        using var recovered = fixture.Spool();

        await recovered.RecoverAsync(CancellationToken.None);

        var commit = Assert.Single(recovered.Commits(directory));
        Assert.True(commit.IsClosed);
        Assert.Equal(8, commit.CommitSequence);
        Assert.Equal(20, new FileInfo(Path.Combine(directory, "video.flv")).Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (await File.ReadAllBytesAsync(Path.Combine(directory, "video.flv")))[..4]);
        Assert.Equal(10, new FileInfo(Path.Combine(directory, "video.index.jsonl")).Length);
        Assert.False(Assert.Single(recovered.Sessions()).State.Descriptor.IsClosed);
    }

    /// <summary>Moving the configured spool root cannot bypass the total retained cache budget.</summary>
    [Fact]
    public void RetainedRootsShareThePersistentSpoolBudget()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var first = spool.EnsureRoot(fixture.DirectoryPath);
        var secondDirectory = Path.Combine(fixture.DirectoryPath, "another-root");
        var second = spool.EnsureRoot(secondDirectory);
        File.WriteAllBytes(Path.Combine(first, "retained.bin"), new byte[600 * 1024]);
        File.WriteAllBytes(Path.Combine(second, "retained.bin"), new byte[600 * 1024]);

        Assert.False(spool.HasCapacity(fixture.Configuration with { TempDirectory = secondDirectory, MaxCacheBytes = 1024 * 1024 }));
        Assert.True(File.Exists(Path.Combine(first, "retained.bin")));
        Assert.True(File.Exists(Path.Combine(second, "retained.bin")));
    }

    /// <summary>A local session can be removed only after a durable server acknowledgement.</summary>
    [Fact]
    public void RefusesToDeleteAnUnacknowledgedSession()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(() => spool.DeleteAcknowledgedSession(session.DirectoryPath));
        Assert.True(Directory.Exists(session.DirectoryPath));

        RecordingSpoolFiles.Write(Path.Combine(session.DirectoryPath, "uploaded.json"), new RecordingUploadReceipt { SessionClosed = true });
        spool.DeleteAcknowledgedSession(session.DirectoryPath);

        Assert.False(Directory.Exists(session.DirectoryPath));
        Assert.Contains("session", Assert.Single(spool.Assignments()).CompletedSessionIds);
    }

    /// <summary>Event snapshots coalesce while an in-flight upload keeps its immutable version alive.</summary>
    [Fact]
    public async Task EventSnapshotsKeepOnlyCurrentAndPinnedVersions()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var session = fixture.Session(spool);
        await using var writer = new SessionEventWriter(Path.Combine(session.DirectoryPath, "弹幕"),
            commit => spool.RecordEventsAsync(session.DirectoryPath, commit));
        await writer.WriteAsync(new RecordedEventEnvelope { Text = "one", OffsetMs = 1 });
        await WaitForAsync(() => spool.Sessions().Single().State.Events?.ThroughSequence >= 1);
        var pinned = spool.PinEvents(session.DirectoryPath);
        var old = RecordingSpoolFiles.Resolve(session.DirectoryPath, pinned.Session.State.EventIndexPath!);
        await writer.WriteAsync(new RecordedEventEnvelope { Text = "two", OffsetMs = 2 });
        await WaitForAsync(() => spool.Sessions().Single().State.Events?.ThroughSequence >= 2);

        Assert.True(File.Exists(old));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(session.DirectoryPath, "弹幕", "snapshots"), "*.jsonl").Count());

        pinned.Dispose();

        Assert.False(File.Exists(old));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(session.DirectoryPath, "弹幕", "snapshots"), "*.jsonl"));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(session.DirectoryPath, "弹幕", "事件.jsonl")).Length);
    }

    /// <summary>Rebuilding an already closed event index after restart preserves its final server seal.</summary>
    [Fact]
    public async Task EventRecoveryPreservesAClosedBroadcastSeal()
    {
        using var fixture = new RecordingWorkerFixture();
        using (var spool = fixture.Spool())
        {
            spool.SaveAssignment(fixture.Assignment, fixture.DirectoryPath);
            var session = fixture.Session(spool);
            await using (var writer = new SessionEventWriter(Path.Combine(session.DirectoryPath, "弹幕"),
                             commit => spool.RecordEventsAsync(session.DirectoryPath, commit)))
            {
                await writer.WriteAsync(new RecordedEventEnvelope { Text = "durable", OffsetMs = 1 });
            }
            spool.CloseSession(session.DirectoryPath, DateTime.UtcNow);
        }
        using var restarted = fixture.Spool();

        await restarted.RecoverAsync(CancellationToken.None);

        var state = Assert.Single(restarted.Sessions()).State;
        Assert.True(state.Descriptor.IsClosed);
        Assert.True(state.Events!.IsClosed);
        Assert.Equal(1, state.Events.ThroughSequence);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
