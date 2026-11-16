using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class WorkerControlGenerationTestContext : IAsyncDisposable
{
    private ClipPlanPreparation? preparation;
    private Func<WorkerHeartbeat, Func<bool>?, CancellationToken, Task<WorkerState>> process = null!;

    private WorkerControlGenerationTestContext(JobTestContext jobs)
    {
        Jobs = jobs;
        Workers = new(jobs.Mongo, jobs.Paths);
    }

    internal JobTestContext Jobs { get; }
    internal TranscodeWorkerService Workers { get; }
    internal string WorkerId { get; private set; } = "";
    internal string Secret { get; private set; } = "";
    internal string Kind { get; private set; } = "";
    internal WorkerHeartbeat Heartbeat { get; private set; } = new();

    internal static async Task<WorkerControlGenerationTestContext> CreateAsync(string kind)
    {
        var fixture = new WorkerControlGenerationTestContext(await JobTestContext.CreateAsync()) { Kind = kind };
        var created = await fixture.Workers.CreateAsync("generation-" + kind, kind, CancellationToken.None);
        fixture.WorkerId = created.Worker.Id;
        fixture.Secret = created.Secret;
        var welcome = await fixture.Workers.ConnectAsync(new()
        {
            WorkerId = fixture.WorkerId, Secret = fixture.Secret, Kind = kind, ExpectedConfigurationRevision = 1,
            InitialConfiguration = new() { TempDirectory = fixture.Jobs.DirectoryPath, MinimumFreeBytes = 0 },
            Capabilities = new()
            {
                ClippingVersion = 1, RecordingVersion = 1, HlsPackagingVersion = 1,
                Encoders = [new() { Encoder = "h264_nvenc", Available = true }]
            }
        }, CancellationToken.None);
        var secretHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Secret)));
        switch (kind)
        {
            case "Transcoding":
            {
                var assets = new TranscodeAssetRegistry(fixture.Jobs.Mongo, fixture.Jobs.Paths);
                var scheduler = new TranscodeJobScheduler(fixture.Jobs.Mongo, fixture.Jobs.Settings, fixture.Jobs.Paths,
                    new LibraryService(fixture.Jobs.Mongo, TimeProvider.System), assets, fixture.Workers,
                    new(fixture.Jobs.Mongo, fixture.Workers, assets));
                fixture.process = (heartbeat, guard, token) => scheduler.HeartbeatAsync(fixture.WorkerId, heartbeat, token, secretHash, guard);
                var (session, _) = await fixture.Jobs.SeedSessionAsync(2);
                await scheduler.QueueAsync([session.Id], null, false, CancellationToken.None);
                break;
            }
            case "Clipping":
            {
                var inputs = new ClipInputPlanner(fixture.Jobs.Mongo, fixture.Jobs.Paths, new ClippingTestRangePlanner());
                var leases = new ClipLeaseService(fixture.Jobs.Mongo, fixture.Workers, inputs);
                fixture.preparation = new(inputs, NullLogger<ClipPlanPreparation>.Instance);
                await fixture.preparation.StartAsync(CancellationToken.None);
                var scheduler = new ClippingJobScheduler(fixture.Jobs.Mongo, fixture.Workers, inputs, leases,
                    fixture.Jobs.Paths, fixture.Jobs.Publisher, fixture.preparation);
                fixture.process = (heartbeat, guard, token) => scheduler.HeartbeatAsync(fixture.WorkerId, heartbeat, token, secretHash, guard);
                var (session, segments) = await fixture.Jobs.SeedSessionAsync();
                for (var index = 0; index < 2; index++)
                {
                    var job = JobTestContext.CreateJob(session, segments);
                    await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
                    var persisted = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
                    await fixture.preparation.WaitForReadyAsync(persisted).WaitAsync(TimeSpan.FromSeconds(5));
                }
                break;
            }
            case "Recording":
            {
                var ownership = new RecordingOwnershipService(fixture.Jobs.Mongo, fixture.Jobs.Paths, new JobTestSecretProtector());
                var scheduler = new RecordingJobScheduler(fixture.Jobs.Mongo, fixture.Jobs.Paths, fixture.Jobs.Settings,
                    fixture.Workers, ownership, new(fixture.Jobs.Mongo, new JobTestSecretProtector(), fixture.Jobs.Settings),
                    fixture.Jobs.Publisher, new(fixture.Jobs.Mongo, fixture.Jobs.Paths, fixture.Jobs.Publisher));
                fixture.process = (heartbeat, guard, token) => scheduler.HeartbeatAsync(fixture.WorkerId, heartbeat, token, secretHash, guard);
                await fixture.Jobs.Mongo.Collection<Streamer>("streamers").InsertManyAsync(
                    [new() { RoomId = 901, Name = "first" }, new() { RoomId = 902, Name = "second" }]);
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        fixture.Heartbeat = new() { AppliedConfigurationRevision = welcome.ConfigurationRevision, Telemetry = new() { FreeBytes = long.MaxValue } };
        var active = await fixture.HeartbeatAsync(null, CancellationToken.None);
        fixture.Heartbeat.Jobs = active.Assignments.Select(value => new WorkerJobProgress
        {
            JobId = value.JobId, Attempt = value.Attempt, LeaseToken = value.LeaseToken, Stage = "Encoding", Progress = 31
        }).Concat(active.ClipAssignments.Select(value => new WorkerJobProgress
        {
            JobId = value.JobId, Attempt = value.Attempt, LeaseToken = value.LeaseToken, Progress = 31
        })).ToList();
        fixture.Heartbeat.Recordings = active.RecordingAssignments.Select(value => new Yohuke.LiveRecorder.Recording.Contracts.RecordingRoomProgress
        {
            AssignmentId = value.AssignmentId, Generation = value.Generation, Token = value.Token,
            ConfigurationRevision = value.ConfigurationRevision, State = "Recording", PendingUploadBytes = 61
        }).ToList();
        if (fixture.Heartbeat.Jobs.Count + fixture.Heartbeat.Recordings.Count != 1)
        {
            throw new InvalidOperationException("The generation fixture must own exactly one active task before replacement.");
        }
        fixture.Heartbeat.Telemetry.CpuPercent = 37;
        return fixture;
    }

    internal Task<WorkerState> HeartbeatAsync(Func<bool>? guard, CancellationToken cancellationToken) =>
        process(Heartbeat, guard, cancellationToken);

    internal async Task<string> SnapshotAsync()
    {
        var state = new BsonDocument();
        foreach (var name in new[] { "transcodeWorkers", "transcodeJobs", "jobs", "recordingAssignments", "recordingOwnerships", "streamers" })
        {
            var documents = await Jobs.Mongo.Collection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync();
            state[name] = new BsonArray(documents);
        }
        return state.ToJson();
    }

    public async ValueTask DisposeAsync()
    {
        if (preparation is not null)
        {
            await preparation.StopAsync(CancellationToken.None);
            preparation.Dispose();
        }
        await Jobs.DisposeAsync();
    }
}
