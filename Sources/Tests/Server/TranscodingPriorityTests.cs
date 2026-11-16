using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证清晰度优先级对全局队列、动态配置及原子租约分配的影响。</summary>
public sealed class TranscodingPriorityTests
{
    /// <summary>存量任务按当前清晰度配置跨场次排序，不依赖建任务时保存的优先级快照。</summary>
    [Fact]
    public async Task CurrentProfilePriorityOverridesJobSnapshotsAcrossSessions()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        var lowProfile = new TranscodeProfile { Priority = 100 };
        var highProfile = new TranscodeProfile { Id = "1080p", Name = "1080p", Height = 1080 };
        fixture.Settings.Settings.Transcoding.Profiles = [lowProfile, highProfile];
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var older = await SeedJobAsync(fixture, lowProfile, DateTime.UtcNow.AddHours(-2));
        var newer = await SeedJobAsync(fixture, highProfile, DateTime.UtcNow.AddHours(-1));

        lowProfile.Priority = -10;
        highProfile.Priority = 10;
        var assignment = await ClaimAsync(fixture, scheduler);

        Assert.NotEqual(older.SessionId, newer.SessionId);
        Assert.Equal(newer.Id, assignment.JobId);
        Assert.Equal(0, assignment.Profile.Priority);
        Assert.Equal("Queued", (await ReadJobAsync(fixture, older.Id)).State);
    }

    /// <summary>同优先级先按创建时间排序，同一时刻再按稳定任务标识排序。</summary>
    [Fact]
    public async Task EqualPrioritiesUseCreationTimeThenStableJobId()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        fixture.Settings.Settings.Transcoding.Profiles =
        [
            new() { Priority = 10 },
            new() { Id = "1080p", Name = "1080p", Priority = 10 }
        ];
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var time = DateTime.UtcNow.Date;
        var newest = await SeedJobAsync(fixture, new(), time.AddHours(1), "a-newest");
        var second = await SeedJobAsync(fixture, new(), time, "z-second");
        var first = await SeedJobAsync(fixture, new() { Id = "1080p" }, time, "b-first");

        Assert.Equal(first.Id, (await ClaimAsync(fixture, scheduler)).JobId);
        Assert.Equal(second.Id, (await ClaimAsync(fixture, scheduler)).JobId);
        Assert.Equal(newest.Id, (await ClaimAsync(fixture, scheduler)).JobId);
    }

    /// <summary>默认及已删除档位共同使用零优先级，仍排在负优先级档位之前。</summary>
    [Fact]
    public async Task MissingProfilesUseZeroPriorityWithoutLosingFifoOrder()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        fixture.Settings.Settings.Transcoding.Profiles =
        [
            new(),
            new() { Id = "1080p", Name = "1080p", Priority = int.MinValue }
        ];
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var time = DateTime.UtcNow.Date;
        var negative = await SeedJobAsync(fixture, new() { Id = "1080p" }, time);
        var removed = await SeedJobAsync(fixture, new() { Id = "removed", Priority = int.MaxValue }, time.AddHours(1));
        var standard = await SeedJobAsync(fixture, new(), time.AddHours(2));

        Assert.Equal(removed.Id, (await ClaimAsync(fixture, scheduler)).JobId);
        Assert.Equal(standard.Id, (await ClaimAsync(fixture, scheduler)).JobId);
        Assert.Equal(negative.Id, (await ClaimAsync(fixture, scheduler)).JobId);
    }

    /// <summary>没有自定义优先级的旧配置与已删除档位保持全局先到先得。</summary>
    [Fact]
    public async Task DefaultPrioritiesPreserveTheExistingGlobalFifoQueue()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        var time = DateTime.UtcNow.Date;
        var older = await SeedJobAsync(fixture, new() { Id = "legacy" }, time);
        var newer = await SeedJobAsync(fixture, new(), time.AddHours(1));

        Assert.Equal(older.Id, (await ClaimAsync(fixture, scheduler)).JobId);
        Assert.Equal(newer.Id, (await ClaimAsync(fixture, scheduler)).JobId);
    }

    /// <summary>修改优先级影响下一次分配，但不会撤销仍有效的执行租约。</summary>
    [Fact]
    public async Task PriorityChangesReorderWaitingJobsWithoutInterruptingActiveLeases()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        var firstProfile = new TranscodeProfile { Priority = 10 };
        var nextProfile = new TranscodeProfile { Id = "1080p", Name = "1080p" };
        fixture.Settings.Settings.Transcoding.Profiles = [firstProfile, nextProfile];
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var time = DateTime.UtcNow.Date;
        var first = await SeedJobAsync(fixture, firstProfile, time);
        var waiting = await SeedJobAsync(fixture, firstProfile, time.AddHours(1));
        var next = await SeedJobAsync(fixture, nextProfile, time.AddHours(2));
        var worker = await CreateWorkerAsync(fixture);
        var original = Assert.Single((await scheduler.HeartbeatAsync(worker.Id, Heartbeat(), CancellationToken.None)).Assignments);
        var originalJob = await ReadJobAsync(fixture, first.Id);

        fixture.Settings.Settings.Transcoding = new()
        {
            Profiles = [firstProfile with { Priority = 0 }, nextProfile with { Priority = 100 }],
            QualityOrder = ["original", "720p", "1080p"]
        };
        var state = await scheduler.HeartbeatAsync(worker.Id, Heartbeat(new WorkerJobProgress
        {
            JobId = original.JobId, Attempt = original.Attempt, LeaseToken = original.LeaseToken, Stage = "Encoding"
        }), CancellationToken.None);

        Assert.Equal(first.Id, original.JobId);
        Assert.True(Assert.Single(state.Leases).Accepted);
        Assert.Equal(next.Id, Assert.Single(state.Assignments).JobId);
        var active = await ReadJobAsync(fixture, first.Id);
        Assert.Equal("Running", active.State);
        Assert.Equal(originalJob.WorkerId, active.WorkerId);
        Assert.Equal(originalJob.Attempt, active.Attempt);
        Assert.Equal(originalJob.LeaseTokenHash, active.LeaseTokenHash);
        Assert.Equal("Queued", (await ReadJobAsync(fixture, waiting.Id)).State);
    }

    /// <summary>过期执行或发布租约重新领取时仍遵循当前清晰度优先级并递增尝试次数。</summary>
    [Theory]
    [InlineData("Running")]
    [InlineData("Publishing")]
    public async Task ExpiredLeasesRejoinTheCurrentPriorityQueue(string state)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        fixture.Settings.Settings.Transcoding.Profiles.Add(new() { Id = "1080p", Name = "1080p", Priority = int.MaxValue });
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var time = DateTime.UtcNow.Date;
        var older = await SeedJobAsync(fixture, new(), time);
        var expired = await SeedJobAsync(fixture, new() { Id = "1080p" }, time.AddHours(1));
        await Jobs(fixture).UpdateOneAsync(x => x.Id == expired.Id, Builders<TranscodeJob>.Update
            .Set(x => x.State, state).Set(x => x.Attempt, 3).Set(x => x.WorkerId, "old-worker")
            .Set(x => x.LeaseTokenHash, "old-token").Set(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));

        var assignment = await ClaimAsync(fixture, scheduler);

        Assert.Equal(expired.Id, assignment.JobId);
        Assert.Equal(4, assignment.Attempt);
        Assert.NotEqual("old-token", (await ReadJobAsync(fixture, expired.Id)).LeaseTokenHash);
        Assert.Equal("Queued", (await ReadJobAsync(fixture, older.Id)).State);
    }

    /// <summary>取消、终态和有效租约的高优先级任务不能被重新领取。</summary>
    [Fact]
    public async Task IneligibleHighPriorityJobsDoNotBlockTheQueue()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        fixture.Settings.Settings.Transcoding.Profiles.Add(new() { Id = "1080p", Name = "1080p", Priority = 100 });
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var time = DateTime.UtcNow.Date;
        foreach (var state in new[] { "Running", "Publishing", "Cancelled", "Failed", "Completed", "Queued" })
        {
            var job = await SeedJobAsync(fixture, new() { Id = "1080p" }, time);
            await Jobs(fixture).UpdateOneAsync(x => x.Id == job.Id, Builders<TranscodeJob>.Update
                .Set(x => x.State, state).Set(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(5))
                .Set(x => x.CancelRequested, state == "Queued"));
        }
        var eligible = await SeedJobAsync(fixture, new(), time.AddHours(1));

        Assert.Equal(eligible.Id, (await ClaimAsync(fixture, scheduler)).JobId);
    }

    /// <summary>节点不支持的高优先级包装任务不妨碍领取兼容任务。</summary>
    [Fact]
    public async Task PackagingCompatibilityTakesPrecedenceOverPriority()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        fixture.Settings.Settings.Transcoding.Profiles.Add(new() { Id = "1080p", Name = "1080p", Priority = 100 });
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var time = DateTime.UtcNow.Date;
        var custom = await SeedJobAsync(fixture, new() { Id = "1080p", SingleFile = true }, time);
        var standard = await SeedJobAsync(fixture, new(), time.AddHours(1));

        Assert.Equal(standard.Id, (await ClaimAsync(fixture, scheduler, 0)).JobId);
        Assert.Equal(custom.Id, (await ClaimAsync(fixture, scheduler)).JobId);
    }

    /// <summary>多个节点并发请求时，每个任务仍只能产生一个有效分配。</summary>
    [Fact]
    public async Task ConcurrentWorkersClaimDistinctJobs()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var scheduler = await CreateSchedulerAsync(fixture);
        fixture.Settings.Settings.Transcoding.Profiles.Add(new() { Id = "1080p", Name = "1080p", Priority = 100 });
        fixture.Settings.Settings.Transcoding.QualityOrder.Add("1080p");
        var jobs = new List<TranscodeJob>();
        var workers = new List<TranscodeWorker>();
        for (var index = 0; index < 4; index++)
        {
            jobs.Add(await SeedJobAsync(fixture, new() { Id = index % 2 == 0 ? "720p" : "1080p" }, DateTime.UtcNow));
            workers.Add(await CreateWorkerAsync(fixture));
        }

        var states = await Task.WhenAll(workers.Select(worker => scheduler.HeartbeatAsync(worker.Id, Heartbeat(), CancellationToken.None)));
        var assignments = states.Select(state => Assert.Single(state.Assignments)).ToArray();

        Assert.Equal(jobs.Select(job => job.Id).Order(StringComparer.Ordinal), assignments.Select(job => job.JobId).Order(StringComparer.Ordinal));
        Assert.All(assignments, assignment => Assert.Equal(1, assignment.Attempt));
        Assert.All(await Jobs(fixture).Find(FilterDefinition<TranscodeJob>.Empty).ToListAsync(), job => Assert.Equal("Running", job.State));
    }

    private static async Task<TranscodeJobScheduler> CreateSchedulerAsync(JobTestContext fixture)
    {
        var assets = new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths);
        await assets.EnsureIndexesAsync();
        var workers = new TranscodeWorkerService(fixture.Mongo);
        return new(fixture.Mongo, fixture.Settings, fixture.Paths, new LibraryService(fixture.Mongo, TimeProvider.System),
            assets, workers, new(fixture.Mongo, workers, assets));
    }

    private static async Task<TranscodeJob> SeedJobAsync(JobTestContext fixture, TranscodeProfile profile, DateTime createdAtUtc, string? id = null)
    {
        var session = new BroadcastSession { Title = "priority", EndedAtUtc = DateTime.UtcNow, State = "Completed" };
        session.DirectoryPath = Path.Combine("priority", session.Id);
        var directory = Path.Combine(fixture.Root, session.DirectoryPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.flv");
        await File.WriteAllTextAsync(path, "priority-source");
        var segment = new RecordingSegment
        {
            SessionId = session.Id, RelativePath = "source.flv", DurationMs = 1000,
            CommittedBytes = new FileInfo(path).Length, IsClosed = true, Revision = 1
        };
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(segment);
        var asset = await new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths).EnsureAsync(session, segment);
        var job = new TranscodeJob
        {
            SessionId = session.Id, AssetId = asset.Id, SourceVersion = asset.SourceVersion,
            Profile = profile with { }, ProfileName = profile.Name, RecipeHash = TranscodeRecipe.Hash(profile), CreatedAtUtc = createdAtUtc
        };
        if (id is not null)
        {
            job.Id = id;
        }
        await Jobs(fixture).InsertOneAsync(job);
        return job;
    }

    private static async Task<TranscodeWorker> CreateWorkerAsync(JobTestContext fixture, int packagingVersion = 1)
    {
        var worker = new TranscodeWorker
        {
            Name = "priority-worker", Configuration = new() { TempDirectory = fixture.DirectoryPath, MinimumFreeBytes = 0 },
            Capabilities = new() { HlsPackagingVersion = packagingVersion, Encoders = [new() { Encoder = "h264_nvenc", Available = true }] }
        };
        await fixture.Mongo.Collection<TranscodeWorker>("transcodeWorkers").InsertOneAsync(worker);
        return worker;
    }

    private static WorkerHeartbeat Heartbeat(params WorkerJobProgress[] jobs) => new()
    {
        AppliedConfigurationRevision = 1, Telemetry = new() { FreeBytes = long.MaxValue }, Jobs = [.. jobs]
    };

    private static async Task<TranscodeAssignment> ClaimAsync(JobTestContext fixture, TranscodeJobScheduler scheduler, int packagingVersion = 1)
    {
        var worker = await CreateWorkerAsync(fixture, packagingVersion);
        return Assert.Single((await scheduler.HeartbeatAsync(worker.Id, Heartbeat(), CancellationToken.None)).Assignments);
    }

    private static IMongoCollection<TranscodeJob> Jobs(JobTestContext fixture) => fixture.Mongo.Collection<TranscodeJob>("transcodeJobs");

    private static Task<TranscodeJob> ReadJobAsync(JobTestContext fixture, string id) => Jobs(fixture).Find(job => job.Id == id).SingleAsync();
}
