using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class RecordingIngestTestContext : IAsyncDisposable
{
    internal JobTestContext Fixture { get; }
    internal TranscodeWorkerService Workers { get; }
    internal RecordingOwnershipService Ownership { get; }
    internal RecordingIngestService Ingest { get; }
    internal RecordingJobScheduler Scheduler { get; }
    internal Streamer Target { get; private set; } = null!;
    internal string WorkerId { get; private set; } = "";
    internal string Secret { get; private set; } = "";
    internal RecordingLease Lease { get; private set; } = null!;
    internal int Revision { get; private set; }
    internal RecordingSessionDescriptor Session { get; private set; } = null!;

    private RecordingIngestTestContext(JobTestContext fixture)
    {
        Fixture = fixture;
        Workers = new(fixture.Mongo, fixture.Paths);
        Ownership = new(fixture.Mongo, fixture.Paths, new JobTestSecretProtector());
        Ingest = new(fixture.Mongo, fixture.Paths, fixture.Settings, Ownership, fixture.Publisher);
        Scheduler = new(fixture.Mongo, fixture.Paths, fixture.Settings, Workers, Ownership,
            new(fixture.Mongo, new JobTestSecretProtector(), fixture.Settings), fixture.Publisher, new(fixture.Mongo, fixture.Paths, fixture.Publisher));
    }

    internal static async Task<RecordingIngestTestContext> CreateAsync(bool recordDanmu = true)
    {
        var fixture = new RecordingIngestTestContext(await JobTestContext.CreateAsync());
        fixture.Target = new() { RoomId = 901, Uid = 42, Name = "录制节点测试", RecordDanmu = recordDanmu };
        await fixture.Fixture.Mongo.Collection<Streamer>("streamers").InsertOneAsync(fixture.Target);
        var created = await fixture.Workers.CreateAsync("Recording test", "Recording", CancellationToken.None);
        fixture.WorkerId = created.Worker.Id;
        fixture.Secret = created.Secret;
        var welcome = await fixture.Workers.ConnectAsync(new()
        {
            WorkerId = fixture.WorkerId, Secret = fixture.Secret, Kind = "Recording", ExpectedConfigurationRevision = 1,
            InitialConfiguration = new() { TempDirectory = Path.Combine(fixture.Fixture.DirectoryPath, "spool"), MinimumFreeBytes = 0,
                FfmpegPath = "", FfprobePath = "", GpuConcurrency = 0, CpuConcurrency = 0 },
            Capabilities = new() { RecordingVersion = 1 }
        }, CancellationToken.None);
        fixture.Revision = welcome.ConfigurationRevision;
        fixture.Lease = (await fixture.HeartbeatAsync()).RecordingAssignments.Single();
        fixture.Session = new()
        {
            SessionId = Guid.NewGuid().ToString("N"), RoomId = fixture.Target.RoomId, LiveTime = 1770000000,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1), Title = "真实提交协议", StreamerName = fixture.Target.Name,
            StreamerUid = 42, HasDanmu = recordDanmu
        };
        await fixture.Ingest.RegisterSessionAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token,
            fixture.Session, CancellationToken.None);
        return fixture;
    }

    internal Task<WorkerState> HeartbeatAsync() => Scheduler.HeartbeatAsync(WorkerId, new()
    {
        AppliedConfigurationRevision = Revision, Telemetry = new() { FreeBytes = long.MaxValue }
    }, CancellationToken.None);

    internal async Task<RecordingSegmentCommit> UploadAsync(bool closed, string? segmentId = null, byte marker = 1)
    {
        segmentId ??= Guid.NewGuid().ToString("N");
        var prefix = Prefix(marker);
        var media = prefix.Concat(new byte[21]).ToArray();
        var index = Encoding.UTF8.GetBytes($"{{\"kind\":\"Committed\",\"offset\":{media.Length},\"timestampMs\":1000}}\n");
        var hash = Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant();
        foreach (var artifact in new[] { ("media", (string?)null, media), ("index", (string?)null, index), ("prefix", hash, prefix) })
        {
            await using var input = new MemoryStream(artifact.Item3);
            await Ingest.UploadAsync(WorkerId, Lease.AssignmentId, Lease.Generation, Lease.Token, Session.SessionId,
                segmentId, artifact.Item1, artifact.Item2, 0, artifact.Item3.Length, input, CancellationToken.None);
        }
        return new()
        {
            SessionId = Session.SessionId, SegmentId = segmentId, RelativePath = "录像/node.flv", IndexRelativePath = "录像/node.index.jsonl",
            CommitSequence = 1, MediaCommittedBytes = media.Length, IndexCommittedBytes = index.Length,
            PrefixLength = prefix.Length, PrefixSha256 = hash, DurationMs = 1000, IsClosed = closed
        };
    }

    internal Task<RecordingCommitAcknowledgement> CommitAsync(RecordingSegmentCommit commit) =>
        Ingest.CommitAsync(WorkerId, Lease.AssignmentId, Lease.Generation, Lease.Token, commit, CancellationToken.None);

    internal async Task SealSessionAsync()
    {
        await Ingest.CommitEventsAsync(WorkerId, Lease.AssignmentId, Lease.Generation, Lease.Token, Session.SessionId,
            new() { IsClosed = true, IndexSha256 = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant() }, CancellationToken.None);
        Session = Session with { IsClosed = true, EndedAtUtc = Session.StartedAtUtc.AddSeconds(1) };
        await Ingest.RegisterSessionAsync(WorkerId, Lease.AssignmentId, Lease.Generation, Lease.Token, Session, CancellationToken.None);
    }

    internal static byte[] Prefix(byte marker)
    {
        var prefix = new byte[32];
        "FLV"u8.CopyTo(prefix);
        prefix[3] = 1;
        prefix[4] = 5;
        prefix[8] = 9;
        prefix[13] = 18;
        prefix[16] = 4;
        prefix[24] = marker;
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(28), 15);
        return prefix;
    }

    public async ValueTask DisposeAsync()
    {
        Ingest.Dispose();
        await Fixture.DisposeAsync();
    }
}
