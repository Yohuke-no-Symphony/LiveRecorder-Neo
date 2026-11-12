using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class JobTestContext : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-job-tests", Guid.NewGuid().ToString("N"));
    internal string DatabaseName { get; } = "media_jobs_tests_" + Guid.NewGuid().ToString("N");
    internal BootstrapStore Bootstrap { get; }
    internal MongoContext Mongo { get; }
    internal JobTestSettingsStore Settings { get; }
    internal JobTestPublisher Publisher { get; } = new();
    internal StorageService Storage { get; }
    internal JobTestMediaProcessor Processor { get; } = new();
    private readonly List<MediaJobWorker> workers = [];

    private JobTestContext()
    {
        Directory.CreateDirectory(Root);
        Bootstrap = new(new AppPaths(Path.Combine(Root, ".config")), new JobTestSecretProtector());
        Mongo = new(Bootstrap);
        Settings = new(Root);
        Storage = new(Mongo, Settings);
    }

    internal static async Task<JobTestContext> CreateAsync()
    {
        var fixture = new JobTestContext();
        await fixture.Bootstrap.SaveAsync(new()
        {
            Installed = true,
            MongoConnectionString = "mongodb://127.0.0.1:34784",
            DatabaseName = fixture.DatabaseName,
            StoragePath = fixture.Root
        });
        await fixture.Mongo.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        return fixture;
    }

    internal async Task<(BroadcastSession Session, List<RecordingSegment> Segments)> SeedSessionAsync(int count = 1)
    {
        var session = new BroadcastSession
        {
            Title = "fixture",
            DirectoryPath = "主播_1/20261112_直播_fixture",
            StartedAtUtc = DateTime.UtcNow.AddHours(-1),
            EndedAtUtc = DateTime.UtcNow,
            State = "Completed"
        };
        var segments = new List<RecordingSegment>();
        var directory = Path.Combine(Root, session.DirectoryPath, "source");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(directory, $"canonical_{index}.flv");
            await File.WriteAllTextAsync(path, "canonical-original-" + index);
            segments.Add(new()
            {
                SessionId = session.Id,
                AttemptId = "original-attempt",
                RelativePath = Path.GetRelativePath(Path.Combine(Root, session.DirectoryPath), path),
                StartOffsetMs = index * 1500,
                DurationMs = 1000,
                CommittedBytes = new FileInfo(path).Length,
                IsClosed = true,
                Revision = 1
            });
        }
        await Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(segments);
        return (session, segments);
    }

    internal static MediaJob CreateJob(BroadcastSession session, List<RecordingSegment> segments, string kind = "Clip")
    {
        return new()
        {
            OwnerId = "test-owner",
            SessionId = session.Id,
            Title = "clip",
            Kind = kind,
            StartMs = segments.Min(segment => segment.StartOffsetMs),
            EndMs = segments.Max(segment => segment.StartOffsetMs + segment.DurationMs),
            SegmentIds = segments.Select(segment => segment.Id).ToList(),
            SourceSegments = segments,
            SourceRevision = 1,
            MutationKey = kind == "Repair" ? session.Id : null
        };
    }

    internal MediaJobWorker CreateWorker()
    {
        var worker = new MediaJobWorker(Mongo, Bootstrap, Settings, Processor, Publisher, Storage, NullLogger<MediaJobWorker>.Instance);
        workers.Add(worker);
        return worker;
    }

    internal async Task<(MediaJobWorker Worker, Task Execution)> ExecuteOwnedAsync(MediaJob job)
    {
        var worker = CreateWorker();
        var owner = (string)typeof(MediaJobWorker).GetField("workerId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        job.State = "Running";
        job.Attempt = Math.Max(1, job.Attempt);
        job.LeaseOwner = owner;
        job.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(90);
        await Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var task = (Task)typeof(MediaJobWorker).GetMethod("ExecuteJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, [job, CancellationToken.None])!;
        return (worker, task);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in workers)
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
        await new MongoClient("mongodb://127.0.0.1:34784").DropDatabaseAsync(DatabaseName);
        Directory.Delete(Root, true);
    }
}
