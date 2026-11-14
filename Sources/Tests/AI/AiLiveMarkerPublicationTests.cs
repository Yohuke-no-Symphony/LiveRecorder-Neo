using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

[Trait("Category", "Integration")]
public sealed class AiLiveMarkerPublicationTests
{
    [Fact]
    public async Task WorkerPublishesCompleteMarkersWithNativeYamlAndGeneratorOwnership()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", "生成者", AiJobPurposes.LIVE_MARKERS, false, CancellationToken.None);
        using var worker = fixture.Worker(new AiTestRunner(async (job, checkpoint, token) =>
        {
            await CompleteAsync(fixture, job);
            await checkpoint.SaveAsync(job, token);
        }));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Completed", job.State);
            Assert.True(job.PublicationStarted);
            var file = Assert.Single(await fixture.MarkerFiles.ListAsync(session.DirectoryPath));
            Assert.Equal(AiLiveMarkerPublisher.FileId(job.Id), file.Id);
            Assert.Equal(file.Id, job.LiveMarkerFileId);
            Assert.Equal(file.Id, job.Result.LiveMarkerFileId);
            Assert.Equal("owner", file.UploaderId);
            Assert.Equal("生成者", file.UploaderName);
            var bytes = await fixture.MarkerFiles.ReadOriginalAsync(session.DirectoryPath, file);
            var native = new LiveMarkerYamlParser().Parse(bytes, TimeZoneInfo.Utc);
            Assert.Equal(session.StartedAtUtc.AddSeconds(1), Assert.Single(native.Entries).RealDateTime.UtcDateTime);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartCompletesLocalPublicationWithoutReplayingModels(bool alreadyPublished)
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var job = await PreparedAsync(fixture, session);
        if (alreadyPublished)
        {
            var fileId = await fixture.MarkerPublisher.PublishAsync(job, CancellationToken.None);
            var file = await fixture.MarkerFiles.ReadMetadataAsync(session.DirectoryPath, fileId);
            await fixture.MarkerFiles.WriteMetadataAsync(session.DirectoryPath, file with { CalibrationMs = 500 });
        }
        job.State = "Running";
        job.PublicationStarted = alreadyPublished;
        job.Context.Phase = alreadyPublished ? "Publishing" : "Completed";
        job.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Jobs.ReplaceOneAsync(item => item.Id == job.Id, job);
        using var worker = fixture.Worker(new AiTestRunner((_, _, _) => throw new InvalidOperationException("Must not replay the model.")));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var completed = await fixture.WaitForTerminalAsync(job.Id);
            Assert.Equal("Completed", completed.State);
            Assert.Equal(0, completed.ModelRequests);
            var file = Assert.Single(await fixture.MarkerFiles.ListAsync(session.DirectoryPath));
            Assert.Equal(alreadyPublished ? 500 : 0, file.CalibrationMs);
            Assert.Equal(AiLiveMarkerPublisher.FileId(job.Id), file.Id);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PublishingCannotBeCancelledOrDeletedAndCompletedDeletionDoesNotResurrect()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var job = await PreparedAsync(fixture, session);
        job.State = "Running";
        job.PublicationStarted = true;
        job.Context.Phase = "Publishing";
        await fixture.Jobs.ReplaceOneAsync(item => item.Id == job.Id, job);
        var id = await fixture.MarkerPublisher.PublishAsync(job, CancellationToken.None);
        var records = new SessionLiveMarkerService(fixture.Library, fixture.Settings, fixture.Workspace.Paths, fixture.MarkerFiles, new(), fixture.Mongo);
        var actor = new LiveMarkerActor("owner", "生成者", false);
        var cancel = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().CancelAsync(job.Id, false, true, CancellationToken.None));
        Assert.Equal("AI_PUBLICATION_IN_PROGRESS", cancel.Code);
        var deletion = await Assert.ThrowsAsync<InfrastructureException>(() => records.DeleteAsync(session.Id, id, actor));
        Assert.Equal("AI_PUBLICATION_IN_PROGRESS", deletion.Code);
        job.State = "Completed";
        job.ActiveKey = null;
        job.LiveMarkerFileId = id;
        job.Result.LiveMarkerFileId = id;
        await fixture.Jobs.ReplaceOneAsync(item => item.Id == job.Id, job);
        await records.DeleteAsync(session.Id, id, actor);
        var status = await fixture.Service().GetAsync(session.Id, AiJobPurposes.LIVE_MARKERS, true, CancellationToken.None);
        Assert.True(status.LatestCompletedStale);
        Assert.Null(status.CurrentJob!.LiveMarkerFileId);
        Assert.Empty(await fixture.MarkerFiles.ListAsync(session.DirectoryPath));
        var missing = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().CreateAsync(session.Id, "owner", "生成者", AiJobPurposes.LIVE_MARKERS, false, CancellationToken.None));
        Assert.Equal("AI_LIVE_MARKER_FILE_MISSING", missing.Code);
        Assert.NotEqual(job.Id, (await fixture.Service().CreateAsync(session.Id, "owner", "生成者", AiJobPurposes.LIVE_MARKERS, true, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task CorruptOriginalCannotRecoverAsCompleteOrBlockAnotherPublication()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var broken = await PreparedAsync(fixture, first);
        var ready = await PreparedAsync(fixture, second);
        var fileId = await fixture.MarkerPublisher.PublishAsync(broken, CancellationToken.None);
        File.Delete(Path.Combine(fixture.Workspace.Paths.RecordsDirectory, first.DirectoryPath, LiveMarkerFileStore.DIRECTORY_NAME, fileId, LiveMarkerFileStore.ORIGINAL_FILE_NAME));
        foreach (var job in new[] { broken, ready })
        {
            job.State = "Running";
            job.PublicationStarted = true;
            job.Context.Phase = "Publishing";
            job.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(-1);
            await fixture.Jobs.ReplaceOneAsync(item => item.Id == job.Id, job);
        }
        using var worker = fixture.Worker(new AiTestRunner((_, _, _) => throw new InvalidOperationException("No model call during publication.")));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var failed = await fixture.WaitForTerminalAsync(broken.Id);
            Assert.Equal("Failed", failed.State);
            Assert.Equal("AI_LIVE_MARKER_SAVE_FAILED", failed.ErrorCode);
            Assert.False(failed.Result.IsComplete);
            Assert.Null(failed.LiveMarkerFileId);
            Assert.Equal("Completed", (await fixture.WaitForTerminalAsync(ready.Id)).State);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialOrCancellationBeforePublicationDoesNotWriteAFile(bool cancel)
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", "生成者", AiJobPurposes.LIVE_MARKERS, false, CancellationToken.None);
        using var worker = fixture.Worker(new AiTestRunner(async (job, checkpoint, token) =>
        {
            await CompleteAsync(fixture, job);
            if (!cancel)
            {
                job.State = "Partial";
                job.Result.IsComplete = false;
            }
            await checkpoint.SaveAsync(job, token);
            if (cancel)
            {
                await fixture.Service().CancelAsync(job.Id, false, true, token);
            }
        }));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal(cancel ? "Cancelled" : "Partial", job.State);
            Assert.Single(job.Result.LiveMarkers);
            Assert.False(job.Result.IsComplete);
            Assert.Empty(await fixture.MarkerFiles.ListAsync(session.DirectoryPath));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<AiAnalysisJob> PreparedAsync(AiMongoFixture fixture, BroadcastSession session)
    {
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", "生成者", AiJobPurposes.LIVE_MARKERS, false, CancellationToken.None);
        var job = await fixture.Jobs.Find(item => item.Id == summary.Id).SingleAsync();
        await CompleteAsync(fixture, job);
        return job;
    }

    private static async Task CompleteAsync(AiMongoFixture fixture, AiAnalysisJob job)
    {
        var evidence = Assert.Single((await fixture.Workspace.Data.ReadAsync(job.Snapshot, 0, job.Snapshot.DurationMs, "All", null, null, 10, CancellationToken.None)).Items);
        job.State = "Completed";
        job.Result.IsComplete = true;
        job.Result.CompletedAtUtc = DateTime.UtcNow;
        job.Context = new() { Phase = "Completed", Segments = [new() { Index = 0, StartMs = 0, EndMs = job.Snapshot.DurationMs, State = "Completed" }] };
        job.Result.LiveMarkers = [new() { Id = "s0:a", Message = "观众讨论歌曲", OffsetMs = evidence.OffsetMs, AnchorEvidenceId = evidence.Id, Evidence = [evidence] }];
    }
}
