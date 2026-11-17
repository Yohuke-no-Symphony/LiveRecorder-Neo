using System.Text.Json.Nodes;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

[Trait("Category", "Integration")]
public sealed class AiAnalysisWorkerTests
{
    [Fact]
    public async Task CancellationAfterTheFinalCheckpointCannotBeOverwrittenByCompletion()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        using var worker = fixture.Worker(new AiTestRunner(async (job, checkpoint, cancellationToken) =>
        {
            job.State = "Completed";
            job.Result.IsComplete = true;
            job.Result.CompletedAtUtc = DateTime.UtcNow;
            job.Result.Topics.Add(new() { Id = "saved", Title = "已校验话题" });
            job.Context = new() { Phase = "Completed", Segments = [new() { Index = 0, State = "Completed" }] };
            await checkpoint.SaveAsync(job, cancellationToken);
            await fixture.Service().CancelAsync(job.Id, cancellationToken);
        }));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Cancelled", job.State);
            Assert.False(job.Result.IsComplete);
            Assert.Null(job.Result.CompletedAtUtc);
            Assert.Single(job.Result.Topics);
            Assert.Null(job.ActiveKey);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MaintenanceHonorsCancellationAfterACompleteCheckpointAndContinuesOtherJobs()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var cancelled = await fixture.Service().CreateAsync(first.Id, "owner", false, CancellationToken.None);
        var interrupted = await fixture.Service().CreateAsync(second.Id, "owner", false, CancellationToken.None);
        await fixture.Jobs.UpdateOneAsync(job => job.Id == cancelled.Id, Builders<AiAnalysisJob>.Update
            .Set(job => job.State, "Running").Set(job => job.CancelRequested, true)
            .Set(job => job.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1))
            .Set(job => job.Result.IsComplete, true).Set(job => job.Result.CompletedAtUtc, DateTime.UtcNow)
            .Set(job => job.Context, new() { Phase = "Completed", Segments = [new() { Index = 0, State = "Completed" }] }));
        await fixture.Jobs.UpdateOneAsync(job => job.Id == interrupted.Id, Builders<AiAnalysisJob>.Update
            .Set(job => job.State, "Running").Set(job => job.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1))
            .Set(job => job.ModelRequests, 1));

        await fixture.Execution.MaintainAsync(CancellationToken.None);

        var firstJob = await fixture.Jobs.Find(job => job.Id == cancelled.Id).SingleAsync();
        Assert.Equal("Cancelled", firstJob.State);
        Assert.False(firstJob.Result.IsComplete);
        Assert.Null(firstJob.Result.CompletedAtUtc);
        Assert.Null(firstJob.ActiveKey);
        Assert.Equal("Interrupted", (await fixture.Jobs.Find(job => job.Id == interrupted.Id).SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotatingOrClearingCredentialsStopsTheJobBeforeAnotherPaidCall(bool clear)
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        fixture.Settings.Value.Ai.ApiKey = fixture.Protector.Protect("original");
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        var chat = new AiTestChatClient((_, _) =>
        {
            fixture.Settings.Value.Ai.ApiKey = clear ? "" : fixture.Protector.Protect("replacement");
            return new()
            {
                PromptTokens = 100, CompletionTokens = 50,
                Message = new JsonObject
                {
                    ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "call", ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "get_metrics", ["arguments"] = "{\"fromMs\":0,\"toMs\":60000}" }
                    })
                }
            };
        });
        using var worker = fixture.Worker(new AiAnalysisRunner(chat, fixture.Workspace.Evidence));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Interrupted", job.State);
            Assert.Equal("AI_CONFIGURATION_CHANGED", job.ErrorCode);
            Assert.Equal(1, chat.Calls);
            Assert.Null(job.ActiveKey);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExpiredUnknownRequestIsChargedAndNeverAutomaticallyReplayed()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        await fixture.Jobs.UpdateOneAsync(item => item.Id == summary.Id, Builders<AiAnalysisJob>.Update
            .Set(item => item.State, "Running").Set(item => item.LeaseOwner, "dead-process").Set(item => item.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1))
            .Set(item => item.RequestInFlight, true).Set(item => item.ReservedTokens, (long)int.MaxValue + 2000).Set(item => item.EstimatedTokens, 50));
        var executions = 0;
        using var worker = fixture.Worker(new AiTestRunner((_, _, _) =>
        {
            Interlocked.Increment(ref executions);
            throw new InvalidOperationException("A recovered ambiguous request must not execute.");
        }));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await fixture.WaitForTerminalAsync(summary.Id);
            Assert.Equal("Interrupted", job.State);
            Assert.Equal((long)int.MaxValue + 2050, job.EstimatedTokens);
            Assert.False(job.RequestInFlight);
            Assert.Equal(0, job.ReservedTokens);
            Assert.Null(job.ActiveKey);
            Assert.Equal(0, executions);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SettledInFlightFlagStillRejectsRecoveryOfAnUnknownDispatchedRequest()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var created = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        var worker = await fixture.RegisterNodeAsync();
        var heartbeat = new WorkerHeartbeat
        {
            AppliedConfigurationRevision = worker.ConfigurationRevision,
            Capabilities = new() { AiExecutionVersion = 1 }, Telemetry = new() { FreeBytes = long.MaxValue }
        };
        var assignment = Assert.Single((await fixture.Scheduler.HeartbeatAsync(worker.Id, heartbeat, CancellationToken.None)).AiAssignments);
        var specification = await fixture.Execution.GetExecutionAsync(worker.Id, assignment.JobId, assignment.Attempt,
            assignment.LeaseToken, CancellationToken.None);
        specification.Execution.ModelRequests = 1;
        specification.Execution.RequestInFlight = true;
        specification.Execution.ReservedTokens = 900;
        await fixture.Execution.CheckpointAsync(worker.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken,
            new() { Sequence = 1, Execution = specification.Execution }, CancellationToken.None);
        await fixture.Jobs.UpdateOneAsync(job => job.Id == created.Id,
            Builders<AiAnalysisJob>.Update.Set(job => job.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        await fixture.Execution.MaintainAsync(CancellationToken.None);
        var interrupted = await fixture.Jobs.Find(job => job.Id == created.Id).SingleAsync();
        Assert.Equal("Interrupted", interrupted.State);
        Assert.False(interrupted.RequestInFlight);
        Assert.True(interrupted.UnsettledModelRequest);
        Assert.Equal(900, interrupted.EstimatedTokens);
        Assert.Equal(0, interrupted.ReservedTokens);

        var recovery = await fixture.Execution.RecoverAsync(worker.Id,
            new()
            {
                JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken,
                NextLeaseToken = new('r', 32)
            },
            new()
            {
                JobId = assignment.JobId, Attempt = assignment.Attempt, Sequence = 1, ModelRequests = 1,
                HasKnownResponse = false, HasDispatchedRequest = true
            }, CancellationToken.None);

        Assert.Equal("Rejected", recovery.Disposition);
        var afterRecovery = await fixture.Jobs.Find(job => job.Id == created.Id).SingleAsync();
        Assert.Equal("Interrupted", afterRecovery.State);
        Assert.True(afterRecovery.UnsettledModelRequest);
        Assert.Equal(900, afterRecovery.EstimatedTokens);
        Assert.Empty((await fixture.Scheduler.HeartbeatAsync(worker.Id, heartbeat, CancellationToken.None)).AiAssignments);
    }

    [Fact]
    public async Task GlobalLeaseSerializesTwoWorkersAcrossDifferentSessions()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var one = await fixture.Service().CreateAsync(first.Id, "owner", false, CancellationToken.None);
        var two = await fixture.Service().CreateAsync(second.Id, "owner", false, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var executions = 0;
        var runner = new AiTestRunner(async (job, checkpoint, cancellationToken) =>
        {
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            Interlocked.Increment(ref executions);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await Task.Delay(100, cancellationToken);
            job.State = "Completed";
            job.Result.IsComplete = true;
            job.Context = new() { Phase = "Completed", Segments = [new() { Index = 0, State = "Completed" }] };
            await checkpoint.SaveAsync(job, cancellationToken);
            Interlocked.Decrement(ref active);
        });
        using var workerOne = fixture.Worker(runner);
        using var workerTwo = fixture.Worker(runner);
        await workerOne.StartAsync(CancellationToken.None);
        await workerTwo.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            Assert.Equal(1, executions);
            release.TrySetResult();
            Assert.Equal("Completed", (await fixture.WaitForTerminalAsync(one.Id)).State);
            Assert.Equal("Completed", (await fixture.WaitForTerminalAsync(two.Id)).State);
            Assert.Equal(2, executions);
            Assert.Equal(1, maximum);
        }
        finally
        {
            release.TrySetResult();
            await workerOne.StopAsync(CancellationToken.None);
            await workerTwo.StopAsync(CancellationToken.None);
        }
    }
}
