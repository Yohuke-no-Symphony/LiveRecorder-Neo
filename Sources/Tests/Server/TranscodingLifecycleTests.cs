using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证媒体替换保留请求意图，并撤销旧物理版本的远端执行权。</summary>
public sealed class TranscodingLifecycleTests
{
    /// <summary>长清单恢复分批保存文件元数据，单个播放文件查询不读取整份产物数组。</summary>
    [Fact]
    public async Task CompletedPublicationRecoversBoundedCatalogAndIndividualFileMetadata()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        fixture.Settings.Settings.Transcoding.Enabled = false;
        var (session, segments) = await fixture.SeedSessionAsync();
        var assets = new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths);
        var asset = await assets.EnsureAsync(session, segments[0]);
        var job = new TranscodeJob { SessionId = session.Id, AssetId = asset.Id, SourceVersion = asset.SourceVersion, RecipeHash = "recipe", Attempt = 1, State = "Completed" };
        job.PublicationDirectory = Path.Combine(".derived", asset.Id, asset.SourceVersion, job.RecipeHash, job.Id + "-1");
        var directory = Path.Combine(fixture.Root, job.PublicationDirectory);
        Directory.CreateDirectory(directory);
        var rendition = new PlaybackRendition
        {
            Id = job.Id + "-1", JobId = job.Id, Attempt = job.Attempt, AssetId = asset.Id,
            SourceVersion = asset.SourceVersion, RecipeHash = job.RecipeHash, RelativeDirectory = job.PublicationDirectory,
            PlaybackTimestampOffsetMs = -20
        };
        var names = Enumerable.Range(0, 1003).Select(index => "chunk_" + index.ToString("D5") + ".m4s").Prepend("init.mp4").Prepend("index.m3u8");
        var bytes = new byte[] { 42 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(path, bytes);
            rendition.Artifacts.Add(new() { Name = name, Length = bytes.Length, Sha256 = hash, WriteTicks = File.GetLastWriteTimeUtc(path).Ticks });
        }
        await JsonFiles.WriteAsync(Path.Combine(directory, "completed.json"), rendition);
        await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").InsertOneAsync(job);
        var workers = new TranscodeWorkerService(fixture.Mongo);
        var leases = new TranscodeLeaseService(fixture.Mongo, workers, assets);
        var library = new LibraryService(fixture.Mongo, TimeProvider.System);
        var scheduler = new TranscodeJobScheduler(fixture.Mongo, fixture.Settings, fixture.Paths, library, assets, workers, leases);
        using var maintenance = new TranscodeMaintenanceService(fixture.Mongo, fixture.Bootstrap, fixture.Settings,
            scheduler, new TranscodeLifecycle(fixture.Mongo, fixture.Paths), assets, fixture.Paths,
            NullLogger<TranscodeMaintenanceService>.Instance);
        await maintenance.MaintainAsync(CancellationToken.None);
        var summary = await fixture.Mongo.Collection<PlaybackRendition>("playbackRenditions").Find(x => x.Id == rendition.Id).SingleAsync();
        Assert.Empty(summary.Artifacts);
        Assert.Equal(-20, summary.PlaybackTimestampOffsetMs);
        Assert.Equal(1005, await fixture.Mongo.Collection<PlaybackRenditionFile>("renditionFiles").CountDocumentsAsync(x => x.RenditionId == rendition.Id));
        var catalog = new TranscodeRenditionCatalog(fixture.Mongo, library, assets, fixture.Paths);
        var file = await catalog.ResolveAsync(session.Id, segments[0].Id, rendition.Id, "chunk_01002.m4s", CancellationToken.None);
        Assert.Equal(hash, file.ContentHash);
        Assert.Equal(1, file.Length);
        await maintenance.MaintainAsync(CancellationToken.None);
        Assert.Equal(1005, await fixture.Mongo.Collection<PlaybackRenditionFile>("renditionFiles").CountDocumentsAsync(x => x.RenditionId == rendition.Id));
    }

    /// <summary>显式库操作只处理受影响资产，剩余持久意图交给后续恢复批次。</summary>
    [Fact]
    public async Task ReferenceReconciliationIsScopedToDirtyAssets()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (firstSession, firstSegments) = await fixture.SeedSessionAsync();
        var (secondSession, secondSegments) = await fixture.SeedSessionAsync();
        var previousPath = Path.Combine(fixture.Root, secondSession.DirectoryPath, secondSegments[0].RelativePath);
        secondSession.DirectoryPath += "_second";
        var secondPath = Path.Combine(fixture.Root, secondSession.DirectoryPath, secondSegments[0].RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        File.Copy(previousPath, secondPath);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == secondSession.Id, secondSession);
        var assets = new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths);
        var first = await assets.EnsureAsync(firstSession, firstSegments[0]);
        var second = await assets.EnsureAsync(secondSession, secondSegments[0]);
        var firstJob = new TranscodeJob { AssetId = first.Id, SourceVersion = first.SourceVersion, State = "Running" };
        var secondJob = new TranscodeJob { AssetId = second.Id, SourceVersion = second.SourceVersion, State = "Running" };
        await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").InsertManyAsync([firstJob, secondJob]);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateManyAsync(_ => true,
            Builders<BroadcastSession>.Update.Set(x => x.IsTrashed, true));
        var lifecycle = new TranscodeLifecycle(fixture.Mongo, fixture.Paths);
        await lifecycle.ReconcileUnderLibraryLockAsync(CancellationToken.None, [first.Id]);
        Assert.Equal("Cancelled", (await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == firstJob.Id).SingleAsync()).State);
        Assert.Equal("Running", (await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == secondJob.Id).SingleAsync()).State);
        await lifecycle.ReconcileUnderLibraryLockAsync();
        Assert.Equal("Cancelled", (await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == secondJob.Id).SingleAsync()).State);
    }

    /// <summary>移动后未提交数据库的成品只在原租约有效时保留；过期后清理且不会重新发布。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationCrashKeepsOnlyTheCurrentLiveAttempt(bool expired)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        fixture.Settings.Settings.Transcoding.Enabled = false;
        var (session, segments) = await fixture.SeedSessionAsync();
        var assets = new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths);
        var asset = await assets.EnsureAsync(session, segments[0]);
        var job = new TranscodeJob
        {
            SessionId = session.Id, AssetId = asset.Id, SourceVersion = asset.SourceVersion,
            RecipeHash = "recipe", Attempt = 1, State = "Publishing", WorkerId = "worker",
            LeaseUntilUtc = DateTime.UtcNow.AddMinutes(expired ? -1 : 1)
        };
        job.PublicationDirectory = Path.Combine(".derived", asset.Id, asset.SourceVersion, job.RecipeHash, job.Id + "-1");
        var directory = Path.Combine(fixture.Root, job.PublicationDirectory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "completed.json"), "{}");
        await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").InsertOneAsync(job);
        var workers = new TranscodeWorkerService(fixture.Mongo);
        var leases = new TranscodeLeaseService(fixture.Mongo, workers, assets);
        var scheduler = new TranscodeJobScheduler(fixture.Mongo, fixture.Settings, fixture.Paths,
            new LibraryService(fixture.Mongo, TimeProvider.System), assets, workers, leases);
        using var maintenance = new TranscodeMaintenanceService(fixture.Mongo, fixture.Bootstrap, fixture.Settings,
            scheduler, new TranscodeLifecycle(fixture.Mongo, fixture.Paths), assets, fixture.Paths,
            NullLogger<TranscodeMaintenanceService>.Instance);
        await maintenance.MaintainAsync(CancellationToken.None);
        Assert.Equal(!expired, Directory.Exists(directory));
        Assert.Empty(await fixture.Mongo.Collection<PlaybackRendition>("playbackRenditions").Find(_ => true).ToListAsync());
        var current = await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == job.Id).SingleAsync();
        Assert.Equal(expired ? null : job.PublicationDirectory, current.PublicationDirectory);
    }

    /// <summary>新录播和有既有手动请求的历史源在修复后补转，未请求的历史源不会被自动扫描。</summary>
    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 0)]
    public async Task RepairCarriesOnlyApplicableTranscodeIntent(bool automatic, bool manual, int expected)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        session.AutoTranscodeEligible = automatic;
        session.AutoTranscodeScheduled = true;
        await fixture.Mongo.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session);
        var assets = new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths);
        var oldAsset = await assets.EnsureAsync(session, segments[0]);
        TranscodeJob? previous = null;
        if (manual)
        {
            previous = new()
            {
                SessionId = session.Id, AssetId = oldAsset.Id, SourceVersion = oldAsset.SourceVersion,
                Profile = new(), ProfileName = "720p", State = "Running", WorkerId = "remote",
                LeaseTokenHash = "obsolete", LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1)
            };
            await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").InsertOneAsync(previous);
        }
        var repair = JobTestContext.CreateJob(session, segments, "Repair");
        var (_, execution) = await fixture.ExecuteOwnedAsync(repair);
        await execution;
        Assert.Equal("Completed", (await fixture.Mongo.Collection<MediaJob>("jobs").Find(x => x.Id == repair.Id).SingleAsync()).State);
        var replaced = await fixture.Mongo.Collection<RecordingSegment>("segments")
            .Find(x => x.SessionId == session.Id && x.Revision == 2).ToListAsync();
        Assert.Equal(2, replaced.Count);
        Assert.All(replaced, segment =>
        {
            Assert.NotEqual(oldAsset.Id, segment.AssetId);
            Assert.Equal(expected > 0 ? new[] { "720p" } : [], segment.PendingTranscodeProfileIds);
        });
        if (previous is not null)
        {
            var cancelled = await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == previous.Id).SingleAsync();
            Assert.Equal("Cancelled", cancelled.State);
            Assert.Null(cancelled.LeaseTokenHash);
            Assert.Null(cancelled.LeaseUntilUtc);
        }
        var workers = new TranscodeWorkerService(fixture.Mongo);
        var leases = new TranscodeLeaseService(fixture.Mongo, workers, assets);
        var scheduler = new TranscodeJobScheduler(fixture.Mongo, fixture.Settings, fixture.Paths,
            new LibraryService(fixture.Mongo, TimeProvider.System), assets, workers, leases);
        using var maintenance = new TranscodeMaintenanceService(fixture.Mongo, fixture.Bootstrap, fixture.Settings,
            scheduler, new TranscodeLifecycle(fixture.Mongo, fixture.Paths), assets, fixture.Paths,
            NullLogger<TranscodeMaintenanceService>.Instance);
        await maintenance.MaintainAsync(CancellationToken.None);
        var replacementAssets = replaced.Select(x => x.AssetId).ToArray();
        var queued = await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").Find(x => replacementAssets.Contains(x.AssetId)).ToListAsync();
        Assert.Equal(expected, queued.Count);
        Assert.All(queued, job => Assert.Equal("Queued", job.State));
        await maintenance.MaintainAsync(CancellationToken.None);
        Assert.Equal(expected, await fixture.Mongo.Collection<TranscodeJob>("transcodeJobs").CountDocumentsAsync(x => replacementAssets.Contains(x.AssetId)));
    }
}
