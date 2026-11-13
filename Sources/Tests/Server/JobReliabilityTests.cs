using System.Reflection;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证作业的租约隔离、完整产物校验和不可变输入恢复。</summary>
public sealed class JobReliabilityTests
{
    /// <summary>完成后的晚到进度不能将百分比退回 99 或重新激活作业。</summary>
    [Fact]
    public async Task LateProgressCannotRegressCompletedJob()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var job = new MediaJob { State = "Running", Attempt = 1, LeaseOwner = "owner", LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1) };
        await fixture.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var reporter = new JobProgressReporter(fixture.Mongo, fixture.Publisher, job, "owner");
        reporter.Report(0.8);
        await reporter.FlushAsync(CancellationToken.None);
        await fixture.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(item => item.Id == job.Id, Builders<MediaJob>.Update.Set(item => item.State, "Completed").Set(item => item.Progress, 100));
        reporter.Report(1);
        await reporter.FlushAsync(CancellationToken.None);
        var current = await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal("Completed", current.State);
        Assert.Equal(100, current.Progress);
    }

    /// <summary>租约失效后媒体进程返回成功，也不能发布文件记录或标记完成。</summary>
    [Fact]
    public async Task ExpiredWorkerCannotPublishOrComplete()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        fixture.Processor.BlockExport = true;
        var job = JobTestContext.CreateJob(session, segments);
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await fixture.Processor.ExportEntered.Task;
        await fixture.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(item => item.Id == job.Id, Builders<MediaJob>.Update.Set(item => item.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        fixture.Processor.ContinueExport.TrySetResult();
        await execution;
        var current = await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal("Running", current.State);
        Assert.Equal(0, await fixture.Mongo.Collection<Clip>("clips").CountDocumentsAsync(_ => true));
        Assert.NotEqual(100, current.Progress);
    }

    /// <summary>修复必须逐个探测多产物，第二个损坏时不能激活新媒体版本。</summary>
    [Fact]
    public async Task RepairValidatesAllOutputsBeforeRevisionActivation()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        fixture.Processor.InvalidSecondRepairOutput = true;
        var job = JobTestContext.CreateJob(session, segments, "Repair");
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;
        Assert.Equal(2, fixture.Processor.ProbeCalls);
        var failed = await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal("Failed", failed.State);
        Assert.Equal(nameof(MessageCode.JOB_OUTPUT_INVALID), failed.ErrorCode);
        Assert.Empty(failed.ErrorArguments);
        Assert.Equal(1, (await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(item => item.Id == session.Id).SingleAsync()).MediaRevision);
        Assert.Equal("canonical-original-0", await File.ReadAllTextAsync(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
    }

    /// <summary>局部修复保留未选择的片段，并赋予完整下一版本和发布身份。</summary>
    [Fact]
    public async Task PartialRepairPreservesUnselectedSegmentsInNextRevision()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, original) = await fixture.SeedSessionAsync(2);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(new RecordingSegment
        {
            Id = "stale-unpublished-epoch", SessionId = session.Id, Revision = 2, RelativePath = "missing-stale.flv", DurationMs = 500
        });
        var job = JobTestContext.CreateJob(session, [original[0]], "Repair");
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;
        var current = await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(item => item.Id == session.Id).SingleAsync();
        var next = await fixture.Mongo.Collection<RecordingSegment>("segments").Find(item => item.SessionId == session.Id && item.Revision == 2).ToListAsync();
        Assert.Equal(2, current.MediaRevision);
        Assert.Equal(job.Id, current.MediaRevisionJobId);
        Assert.Equal(1, current.MediaRevisionAttempt);
        Assert.Equal(3, next.Count);
        Assert.Contains(next, item => item.RelativePath == original[1].RelativePath && item.StartOffsetMs == original[1].StartOffsetMs);
        Assert.Equal(2, await fixture.Mongo.Collection<RecordingSegment>("segments").CountDocumentsAsync(item => item.SessionId == session.Id && item.Revision == 1));
        Assert.Equal("Completed", (await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync()).State);
    }

    /// <summary>完成清单可被下一次租约复用，清理残留工作文件而不重新编码。</summary>
    [Fact]
    public async Task RecoveryReusesValidatedManifestAndCleansWorkDirectory()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var job = JobTestContext.CreateJob(session, segments);
        job.Attempt = 1;
        var manifest = JobOutputManifest.Create(job);
        var relative = Path.Combine(session.DirectoryPath, "切片", job.OwnerId, job.Id, "v1", "recovered.mp4");
        var path = Path.Combine(fixture.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "complete-artifact");
        manifest.Paths.Add(relative);
        manifest.ArtifactLengths.Add(relative, new FileInfo(path).Length);
        var work = Path.Combine(fixture.Root, ".work", "jobs", job.Id);
        await JsonFiles.WriteAsync(Path.Combine(work, "1", "completed.json"), manifest);
        Directory.CreateDirectory(Path.Combine(work, "0"));
        await File.WriteAllTextAsync(Path.Combine(work, "0", "orphan.mp4"), "incomplete");
        job.Attempt = 2;
        job.Error = "previous attempt failed";
        job.ErrorCode = nameof(MessageCode.JOB_PROCESSING_FAILED);
        job.ErrorArguments = ["previous attempt diagnostic"];
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;
        var current = await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal("Completed", current.State);
        Assert.Null(current.Error);
        Assert.Null(current.ErrorCode);
        Assert.Empty(current.ErrorArguments);
        Assert.Equal(relative, current.OutputRelativePath);
        Assert.Equal(0, fixture.Processor.ExportCalls);
        Assert.False(Directory.Exists(work));
        Assert.True(File.Exists(path));
    }

    /// <summary>修复版本已经激活但作业未完成时，接管者复用同一发布身份并释放互斥键。</summary>
    [Fact]
    public async Task RecoveryFinishesPreviouslyActivatedRepairWithoutReprocessing()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, original) = await fixture.SeedSessionAsync();
        var job = JobTestContext.CreateJob(session, original, "Repair");
        job.Attempt = 1;
        var manifest = JobOutputManifest.Create(job);
        var relative = Path.Combine(session.DirectoryPath, "修复", job.Id, "v1", "recovered.flv");
        var path = Path.Combine(fixture.Root, relative);
        var indexPath = Path.ChangeExtension(path, ".index.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "complete-repair");
        await File.WriteAllTextAsync(indexPath, "{\"kind\":\"Keyframe\"}\n");
        manifest.Paths.Add(relative);
        manifest.ArtifactLengths.Add(relative, new FileInfo(path).Length);
        manifest.ArtifactLengths.Add(Path.GetRelativePath(fixture.Root, indexPath), new FileInfo(indexPath).Length);
        var repaired = new RecordingSegment
        {
            Id = job.Id + "_v1_0", SessionId = session.Id, Revision = 2, AttemptId = job.Id,
            RelativePath = Path.GetRelativePath(Path.Combine(fixture.Root, session.DirectoryPath), path),
            IndexRelativePath = Path.GetRelativePath(Path.Combine(fixture.Root, session.DirectoryPath), indexPath),
            DurationMs = 1000, CommittedBytes = new FileInfo(path).Length, IsClosed = true
        };
        manifest.RepairedSegments.Add(repaired);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(repaired);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id, Builders<BroadcastSession>.Update
            .Set(item => item.MediaRevision, 2).Set(item => item.MediaRevisionJobId, job.Id).Set(item => item.MediaRevisionAttempt, 1));
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, ".work", "jobs", job.Id, "1", "completed.json"), manifest);
        job.Attempt = 2;
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;
        var completed = await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal("Completed", completed.State);
        Assert.Null(completed.MutationKey);
        Assert.Equal(0, fixture.Processor.RepairCalls);
        Assert.Equal(1, await fixture.Mongo.Collection<RecordingSegment>("segments").CountDocumentsAsync(item => item.SessionId == session.Id && item.Revision == 2));
        Assert.True(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, original[0].RelativePath)));
    }

    /// <summary>完成状态写入后崩溃留下的工作目录会由后续维护清理。</summary>
    [Fact]
    public async Task TerminalWorkSweepRemovesCrashLeftovers()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var job = JobTestContext.CreateJob(session, segments);
        job.State = "Completed";
        job.Attempt = 1;
        await fixture.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var work = Path.Combine(fixture.Root, ".work", "jobs", job.Id, "1", ".export-leftover");
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "partial.mp4"), "partial");
        var worker = fixture.CreateWorker();
        var sweep = (Task)typeof(MediaJobWorker).GetMethod("CleanupTerminalWorkAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [CancellationToken.None])!;
        await sweep;
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".work", "jobs", job.Id)));
        Assert.Equal("canonical-original-0", await File.ReadAllTextAsync(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
    }

    /// <summary>手动分组通过根目录引用读取原文件，局部修复保留未选择的根引用。</summary>
    [Fact]
    public async Task GroupedSessionRepairUsesRootReferencesAndPreservesUntouchedSources()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (originalSession, originals) = await fixture.SeedSessionAsync(2);
        var grouped = new BroadcastSession
        {
            Title = "grouped", DirectoryPath = "分组场次", State = "Completed", StartedAtUtc = originalSession.StartedAtUtc,
            EndedAtUtc = originalSession.EndedAtUtc
        };
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(grouped);
        var references = originals.Select((original, index) => new RecordingSegment
        {
            SessionId = grouped.Id, Revision = 1, IsClosed = true, RelativePath = "reference_" + index + ".flv",
            RootRelativePath = Path.Combine(originalSession.DirectoryPath, original.RelativePath),
            RootIndexRelativePath = Path.Combine(originalSession.DirectoryPath, "source", "index_" + index + ".jsonl"),
            StartOffsetMs = original.StartOffsetMs, DurationMs = original.DurationMs, CommittedBytes = original.CommittedBytes
        }).ToList();
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(references);
        var job = JobTestContext.CreateJob(grouped, [references[0]], "Repair");
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;
        Assert.Equal("Completed", (await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync()).State);
        var kept = await fixture.Mongo.Collection<RecordingSegment>("segments").Find(item => item.SessionId == grouped.Id && item.Revision == 2 && item.RootRelativePath != null).SingleAsync();
        Assert.Equal(references[1].RootRelativePath, kept.RootRelativePath);
        Assert.Equal(references[1].RootIndexRelativePath, kept.RootIndexRelativePath);
        foreach (var original in originals)
        {
            Assert.True(File.Exists(Path.Combine(fixture.Root, originalSession.DirectoryPath, original.RelativePath)));
        }
        Assert.Equal(1, (await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(item => item.Id == originalSession.Id).SingleAsync()).MediaRevision);
    }

    /// <summary>伪造指向 canonical 的恢复清单被拒绝，重新处理也不能覆盖原文件。</summary>
    [Fact]
    public async Task RecoveryCannotTreatCanonicalSourceAsOutput()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var job = JobTestContext.CreateJob(session, segments);
        job.Attempt = 1;
        var manifest = JobOutputManifest.Create(job);
        var relative = Path.Combine(session.DirectoryPath, segments[0].RelativePath);
        manifest.Paths.Add(relative);
        manifest.ArtifactLengths.Add(relative, new FileInfo(Path.Combine(fixture.Root, relative)).Length);
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, ".work", "jobs", job.Id, "1", "completed.json"), manifest);
        job.Attempt = 2;
        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;
        Assert.Equal(1, fixture.Processor.ExportCalls);
        Assert.Equal("canonical-original-0", await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative)));
        Assert.NotEqual(relative, (await fixture.Mongo.Collection<Clip>("clips").Find(item => item.Id == job.Id).SingleAsync()).RelativePath);
    }
}
