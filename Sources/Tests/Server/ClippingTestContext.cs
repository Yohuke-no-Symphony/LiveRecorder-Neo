using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class ClippingTestContext : IAsyncDisposable
{
    internal JobTestContext Jobs { get; }
    internal ClippingTestRangePlanner Ranges { get; } = new();
    internal TranscodeWorkerService Workers { get; }
    internal ClipInputPlanner Inputs { get; }
    internal ClipLeaseService Leases { get; }
    internal ClipPlanPreparation Preparation { get; }
    internal ClippingJobScheduler Scheduler { get; }
    internal ClipTransferService Transfers { get; }
    internal string WorkerId { get; private set; } = "";
    internal string Secret { get; private set; } = "";
    internal int Revision { get; private set; }

    private ClippingTestContext(JobTestContext jobs)
    {
        Jobs = jobs;
        Workers = new(jobs.Mongo, jobs.Paths);
        Inputs = new(jobs.Mongo, jobs.Paths, Ranges);
        Leases = new(jobs.Mongo, Workers, Inputs);
        Preparation = new(Inputs, NullLogger<ClipPlanPreparation>.Instance);
        Scheduler = new(jobs.Mongo, Workers, Inputs, Leases, jobs.Paths, jobs.Publisher, Preparation);
        Transfers = new(jobs.Mongo, Leases, Inputs, jobs.Paths, jobs.Settings, jobs.Processor,
            new(jobs.Mongo, jobs.Paths), jobs.Publisher);
    }

    internal static async Task<ClippingTestContext> CreateAsync()
    {
        var result = new ClippingTestContext(await JobTestContext.CreateAsync());
        await result.Preparation.StartAsync(CancellationToken.None);
        var created = await result.Workers.CreateAsync("Clipping test", "Clipping", CancellationToken.None);
        result.WorkerId = created.Worker.Id;
        result.Secret = created.Secret;
        var welcome = await result.Workers.ConnectAsync(new()
        {
            WorkerId = result.WorkerId, Secret = result.Secret, Kind = "Clipping",
            InitialConfiguration = new()
            {
                TempDirectory = Path.Combine(result.Jobs.DirectoryPath, "remote"), MinimumFreeBytes = 0,
                GpuConcurrency = 0, CpuConcurrency = 0
            },
            ExpectedConfigurationRevision = 1, Capabilities = new() { ClippingVersion = 1 }
        }, CancellationToken.None);
        result.Revision = welcome.ConfigurationRevision;
        await result.HeartbeatAsync();
        return result;
    }

    internal Task<WorkerState> HeartbeatAsync() => Scheduler.HeartbeatAsync(WorkerId, new()
    {
        AppliedConfigurationRevision = Revision, Telemetry = new() { FreeBytes = long.MaxValue }
    }, CancellationToken.None);

    internal async Task<(MediaJob Job, ClipAssignment Assignment)> AssignAsync(bool growing = false)
    {
        var (session, segments) = await Jobs.SeedSessionAsync();
        segments[0].IsClosed = !growing;
        await Jobs.Mongo.Collection<RecordingSegment>("segments").UpdateOneAsync(value => value.Id == segments[0].Id,
            Builders<RecordingSegment>.Update.Set(value => value.IsClosed, !growing));
        var job = JobTestContext.CreateJob(session, segments);
        await Jobs.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var state = await HeartbeatAsync();
        if (state.ClipAssignments.Count == 0)
        {
            await Preparation.WaitForReadyAsync(job).WaitAsync(TimeSpan.FromSeconds(5));
            state = await HeartbeatAsync();
        }
        return (job, state.ClipAssignments.Single());
    }

    public async ValueTask DisposeAsync()
    {
        Transfers.Dispose();
        await Preparation.StopAsync(CancellationToken.None);
        Preparation.Dispose();
        await Jobs.DisposeAsync();
    }
}
