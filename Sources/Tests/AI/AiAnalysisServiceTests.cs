using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

[Trait("Category", "Integration")]
public sealed class AiAnalysisServiceTests
{
    [Fact]
    public async Task RecordingSessionIsRejectedBeforeCreatingASnapshotOrJob()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync(false);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None));
        Assert.Equal("AI_RECORDING_NOT_FINISHED", error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, await fixture.Jobs.CountDocumentsAsync(FilterDefinition<AiAnalysisJob>.Empty));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace.Paths.DataDirectory, "ai", "snapshots")));
    }

    [Fact]
    public async Task ConcurrentCreatorsShareOnePersistentJobAndDoNotStoreCredentials()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        fixture.Settings.Value.Ai.ApiKey = fixture.Protector.Protect("private-test-key");
        var session = await fixture.SeedAsync();
        var created = await Task.WhenAll(fixture.Service().CreateAsync(session.Id, "first", true, CancellationToken.None),
            fixture.Service().CreateAsync(session.Id, "second", true, CancellationToken.None));
        Assert.Equal(created[0].Id, created[1].Id);
        var job = await fixture.Jobs.Find(FilterDefinition<AiAnalysisJob>.Empty).SingleAsync();
        Assert.Equal("", job.Settings.ApiKey);
        Assert.Equal(AiSettingsSecrets.Fingerprint(fixture.Settings.Value.Ai), job.CredentialFingerprint);
        Assert.DoesNotContain(fixture.Settings.Value.Ai.ApiKey, JsonSerializer.Serialize(job));
        Assert.DoesNotContain("private-test-key", JsonSerializer.Serialize(job));
        Assert.NotNull(job.Input);
        Assert.False(job.Input.PreparedSnapshot);
        Assert.Single(job.Input.Sources);
        Assert.Single(job.SourcePaths);
        Assert.Equal(job.Input.Fingerprint, job.Snapshot.SourceFingerprint);
        Assert.Equal("", job.Snapshot.EvidenceRelativePath);
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace.Paths.DataDirectory, "ai", "snapshots")));
        Assert.All(fixture.Publisher.Events, item => Assert.Null(item.Owner));
    }

    [Fact]
    public async Task GroupedJobBudgetUsesTheSumOfSourceDurations()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync(duration: 14400000, sources:
        [
            new() { DirectoryPath = "first", FromMs = 0, ToMs = 3600000 },
            new() { DirectoryPath = "second", FromMs = 0, ToMs = 3600000, StartOffsetMs = 10800000 }
        ]);
        await fixture.Workspace.WriteAsync("first", new RecordedEventEnvelope { Kind = "Danmu", Text = "T:前半", OffsetMs = 1000 });
        await fixture.Workspace.WriteAsync("second", new RecordedEventEnvelope { Kind = "Danmu", Text = "T:后半", OffsetMs = 1000 });
        var segments = fixture.Mongo.Collection<RecordingSegment>("segments");
        await segments.DeleteManyAsync(segment => segment.SessionId == session.Id);
        await segments.InsertManyAsync(
        [
            new() { SessionId = session.Id, DurationMs = 3600000, IsClosed = true, RelativePath = "first.flv" },
            new() { SessionId = session.Id, StartOffsetMs = 10800000, DurationMs = 3600000, IsClosed = true, RelativePath = "second.flv" }
        ]);
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        Assert.Equal(40000, summary.TokenBudget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousInsertPreservesOnlyCommittedManifestsWithoutPreparingSnapshots(bool committed)
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var faulty = new AiInsertFaultContext(fixture.Mongo, committed);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Service(faulty).CreateAsync(session.Id, "owner", false, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace.Paths.DataDirectory, "ai", "snapshots")));
        Assert.Equal(committed ? 1 : 0, await fixture.Jobs.CountDocumentsAsync(FilterDefinition<AiAnalysisJob>.Empty));
        if (committed)
        {
            var job = await fixture.Jobs.Find(FilterDefinition<AiAnalysisJob>.Empty).SingleAsync();
            Assert.NotNull(job.Input);
            Assert.False(job.Input.PreparedSnapshot);
            Assert.Equal("", job.Snapshot.EvidenceRelativePath);
            var source = Assert.Single(job.Input.Sources);
            var path = Path.Combine(fixture.Workspace.Paths.RecordsDirectory, Assert.Single(job.SourcePaths));
            Assert.Equal(new FileInfo(path).Length, source.Length);
            Assert.Equal(new FileInfo(path).LastWriteTimeUtc.Ticks, source.LastWriteUtcTicks);
        }
    }

    [Fact]
    public async Task CompletedResultsRemainReusableAndStalenessBelongsToEachDisplayedResult()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var service = fixture.Service();
        var summary = await service.CreateAsync(session.Id, "first", false, CancellationToken.None);
        await fixture.Jobs.UpdateOneAsync(item => item.Id == summary.Id, Builders<AiAnalysisJob>.Update
            .Set(item => item.State, "Completed").Set(item => item.ActiveKey, null).Set(item => item.Result.IsComplete, true)
            .Set(item => item.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        Assert.Equal(summary.Id, (await service.CreateAsync(session.Id, "second", false, CancellationToken.None)).Id);
        var newer = await service.CreateAsync(session.Id, "second", true, CancellationToken.None);
        var older = new AiAnalysisJob { SessionId = session.Id, State = "Partial", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3) };
        older.Result.Topics.Add(new() { Id = "old" });
        await fixture.Jobs.InsertOneAsync(older);
        await fixture.Jobs.UpdateOneAsync(item => item.Id == summary.Id, Builders<AiAnalysisJob>.Update.Set(item => item.Identity, "old-source"));
        await fixture.Jobs.UpdateOneAsync(item => item.Id == newer.Id, Builders<AiAnalysisJob>.Update
            .Set(item => item.State, "Partial").Set(item => item.ActiveKey, null).Set(item => item.Result.Topics, [new() { Id = "new" }]));
        var status = await service.GetAsync(session.Id, false, CancellationToken.None);
        Assert.Equal(summary.Id, status.LatestCompleted!.JobId);
        Assert.Equal(newer.Id, status.LatestPartial!.JobId);
        Assert.True(status.LatestCompletedStale);
        Assert.False(status.LatestPartialStale);
        Assert.False(status.CanGenerate);
        await fixture.Jobs.DeleteOneAsync(item => item.Id == newer.Id);
        Assert.Null((await service.GetAsync(session.Id, false, CancellationToken.None)).LatestPartial);
    }
}
