using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

[Trait("Category", "Integration")]
public sealed class AiMemoryCheckpointTests
{
    [Fact]
    public async Task PrivateContextPreservesEvidenceCursorsAndOnlyTheCommittedFileVersion()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        using var worker = fixture.Worker(new AiTestRunner(async (job, checkpoint, cancellationToken) =>
        {
            var memory = new AiAnalysisMemory
            {
                Evidence = [new() { Id = "source:1", OffsetMs = 1000, Text = "保留的同传" }],
                Reads = [new() { Tool = "search_messages", FromMs = 0, ToMs = 60000, Kind = "Translation", Query = "歌曲", Cursor = "10", NextCursor = "20", Truncated = true }]
            };
            await checkpoint.SaveMemoryAsync(job, memory, cancellationToken);
            memory.Evidence.Add(new() { Id = "source:2", OffsetMs = 2000, Text = "新增证据" });
            await checkpoint.SaveMemoryAsync(job, memory, cancellationToken);
            var restored = await checkpoint.LoadMemoryAsync(job, cancellationToken);
            Assert.Equal(2, restored.Evidence.Count);
            Assert.Equal("20", Assert.Single(restored.Reads).NextCursor);
            Assert.Equal("歌曲", restored.Reads[0].Query);
            Assert.True(restored.Reads[0].Truncated);
            job.State = "Partial";
        }));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Partial", job.State);
            Assert.Equal(2, job.Context.ConversationVersion);
            var folder = Path.Combine(fixture.Workspace.Paths.DataDirectory, "ai", "contexts", job.Id);
            var file = Assert.Single(Directory.GetFiles(folder));
            Assert.Equal(Path.Combine(fixture.Workspace.Paths.DataDirectory, job.Context.ConversationPath!), file);
            var saved = JsonSerializer.Deserialize<AiAnalysisMemory>(await File.ReadAllTextAsync(file), JsonFiles.Options)!;
            Assert.Equal(job.Id, saved.JobId);
            Assert.Equal(job.Context.ConversationVersion, saved.Version);
            Assert.DoesNotContain(job.Context.ConversationPath!, JsonSerializer.Serialize(AiAnalysisJobSummary.From(job)));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationDuringPublishingRemovesTheUnreferencedContextFile()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        using var worker = fixture.Worker(new AiTestRunner(async (job, checkpoint, cancellationToken) =>
        {
            var memory = new AiAnalysisMemory();
            await checkpoint.SaveMemoryAsync(job, memory, cancellationToken);
            await fixture.Service().CancelAsync(job.Id, cancellationToken);
            await checkpoint.SaveMemoryAsync(job, memory, cancellationToken);
        }));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Cancelled", job.State);
            Assert.Equal(1, job.Context.ConversationVersion);
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.Workspace.Paths.DataDirectory, "ai", "contexts", job.Id)));
            Assert.Equal(0, job.EstimatedTokens);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RecoveryCannotPromoteOneCompletedSegmentToACompleteRecording()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        var context = new AiAnalysisContextState
        {
            Phase = "Analyzing", Segments = [new() { Index = 0, State = "Completed" }, new() { Index = 1, State = "Pending" }]
        };
        await fixture.Jobs.UpdateOneAsync(job => job.Id == summary.Id, Builders<AiAnalysisJob>.Update
            .Set(job => job.State, "Running").Set(job => job.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1))
            .Set(job => job.Context, context).Set(job => job.Result.IsComplete, true));
        using var worker = fixture.Worker(new AiTestRunner((_, _, _) => throw new InvalidOperationException("Do not replay an interrupted analysis.")));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Interrupted", job.State);
            Assert.False(job.Result.IsComplete);
            Assert.Equal("Pending", job.Context.Segments[1].State);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
