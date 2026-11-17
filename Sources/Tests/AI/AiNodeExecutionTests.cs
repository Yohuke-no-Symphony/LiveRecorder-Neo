using MongoDB.Driver;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

/// <summary>验证 AI 节点的全局容量、租约隔离及精确检查点确认。</summary>
[Trait("Category", "Integration")]
public sealed class AiNodeExecutionTests
{
    /// <summary>不同节点同时领取时仍受主站全局并发限制。</summary>
    [Fact]
    public async Task MultipleNodesShareTheGlobalConcurrencyLimit()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Workspace.Paths);
        var execution = Execution(fixture, workers);
        var scheduler = new AiJobScheduler(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, execution, new());
        var session = await fixture.SeedAsync();
        await fixture.Service().CreateAsync(session.Id, "user", false, CancellationToken.None);
        var second = await fixture.SeedAsync();
        await fixture.Service().CreateAsync(second.Id, "user", false, CancellationToken.None);
        var nodeOne = await RegisterAsync(workers, fixture);
        var nodeTwo = await RegisterAsync(workers, fixture);

        var first = await scheduler.HeartbeatAsync(nodeOne.Id, Heartbeat(nodeOne), CancellationToken.None);
        var other = await scheduler.HeartbeatAsync(nodeTwo.Id, Heartbeat(nodeTwo), CancellationToken.None);

        Assert.Single(first.AiAssignments);
        Assert.Empty(other.AiAssignments);
        Assert.Equal(1, await fixture.Jobs.CountDocumentsAsync(value => value.State == "Running"));
    }

    /// <summary>同序号重复提交只确认同一内容，外来节点和冲突内容不能覆盖进度。</summary>
    [Fact]
    public async Task CheckpointAcknowledgementIsIdempotentAndFenced()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Workspace.Paths);
        var execution = Execution(fixture, workers);
        var scheduler = new AiJobScheduler(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, execution, new());
        var session = await fixture.SeedAsync();
        await fixture.Service().CreateAsync(session.Id, "user", false, CancellationToken.None);
        var node = await RegisterAsync(workers, fixture);
        var assigned = Assert.Single((await scheduler.HeartbeatAsync(node.Id, Heartbeat(node), CancellationToken.None)).AiAssignments);
        var specification = await execution.GetExecutionAsync(node.Id, assigned.JobId, assigned.Attempt, assigned.LeaseToken, CancellationToken.None);
        Assert.DoesNotContain(specification.Execution.Settings.GetType().GetProperties(), value => value.Name == "ApiKey");
        var checkpoint = new AiExecutionCheckpoint { Sequence = 1, Execution = specification.Execution };
        var first = await execution.CheckpointAsync(node.Id, assigned.JobId, assigned.Attempt, assigned.LeaseToken, checkpoint, CancellationToken.None);
        var repeated = await execution.CheckpointAsync(node.Id, assigned.JobId, assigned.Attempt, assigned.LeaseToken, checkpoint, CancellationToken.None);
        Assert.Equal(first.Sequence, repeated.Sequence);
        var other = await RegisterAsync(workers, fixture);
        await Assert.ThrowsAsync<InfrastructureException>(() => execution.CheckpointAsync(other.Id, assigned.JobId, assigned.Attempt, assigned.LeaseToken, checkpoint, CancellationToken.None));
        checkpoint.Execution.Progress = 50;
        var conflict = await Assert.ThrowsAsync<InfrastructureException>(() => execution.CheckpointAsync(node.Id, assigned.JobId, assigned.Attempt, assigned.LeaseToken, checkpoint, CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(0, (await fixture.Jobs.Find(value => value.Id == assigned.JobId).SingleAsync()).Progress);
    }

    private static AiJobExecutionService Execution(AiMongoFixture fixture, TranscodeWorkerService workers) =>
        new(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, fixture.Library, fixture.Protector,
            fixture.Publisher, fixture.MarkerPublisher, new(fixture.Workspace.Paths, fixture.Workspace.Data), new());

    /// <summary>撤销节点时保留未知调用事实并拒绝旧租约，不把已付费请求重新排队。</summary>
    [Fact]
    public async Task RevokingWorkerDoesNotReplayAnUnsettledRequest()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Workspace.Paths);
        var execution = Execution(fixture, workers);
        var scheduler = new AiJobScheduler(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, execution, new());
        var session = await fixture.SeedAsync();
        await fixture.Service().CreateAsync(session.Id, "user", false, CancellationToken.None);
        var node = await RegisterAsync(workers, fixture);
        var assignment = Assert.Single((await scheduler.HeartbeatAsync(node.Id, Heartbeat(node), CancellationToken.None)).AiAssignments);
        var specification = await execution.GetExecutionAsync(node.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken, CancellationToken.None);
        specification.Execution.ModelRequests = 1;
        specification.Execution.RequestInFlight = true;
        specification.Execution.ReservedTokens = 1200;
        await execution.CheckpointAsync(node.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken,
            new() { Sequence = 1, Execution = specification.Execution }, CancellationToken.None);

        await workers.RotateAsync(node.Id, CancellationToken.None);

        var saved = await fixture.Jobs.Find(value => value.Id == assignment.JobId).SingleAsync();
        Assert.Equal("Interrupted", saved.State);
        Assert.True(saved.UnsettledModelRequest);
        Assert.False(saved.RequestInFlight);
        Assert.Equal(1200, saved.EstimatedTokens);
        Assert.Null(saved.ActiveKey);
        await Assert.ThrowsAsync<InfrastructureException>(() => execution.GetExecutionAsync(node.Id, assignment.JobId,
            assignment.Attempt, assignment.LeaseToken, CancellationToken.None));
        Assert.Empty((await scheduler.HeartbeatAsync(node.Id, Heartbeat(node), CancellationToken.None)).AiAssignments);
    }

    private static WorkerHeartbeat Heartbeat(TranscodeWorker worker) => new()
    {
        AppliedConfigurationRevision = worker.ConfigurationRevision,
        Capabilities = new() { AiExecutionVersion = 1 }, Telemetry = new() { FreeBytes = long.MaxValue }
    };

    /// <summary>全局可并行而节点保持单任务，下调全局上限不撤销既有任务。</summary>
    [Fact]
    public async Task GlobalAndNodeCapacityAreIndependentAndLoweringDoesNotInterrupt()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        fixture.Settings.Value.Ai.MaxConcurrentJobs = 2;
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Workspace.Paths);
        var execution = Execution(fixture, workers);
        var scheduler = new AiJobScheduler(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, execution, new());
        for (var index = 0; index < 3; index++)
        {
            var session = await fixture.SeedAsync();
            await fixture.Service().CreateAsync(session.Id, "user", false, CancellationToken.None);
        }
        var firstNode = await RegisterAsync(workers, fixture);
        var secondNode = await RegisterAsync(workers, fixture);
        var first = Assert.Single((await scheduler.HeartbeatAsync(firstNode.Id, Heartbeat(firstNode), CancellationToken.None)).AiAssignments);
        Assert.Empty((await scheduler.HeartbeatAsync(firstNode.Id, Heartbeat(firstNode), CancellationToken.None)).AiAssignments);
        Assert.Single((await scheduler.HeartbeatAsync(secondNode.Id, Heartbeat(secondNode), CancellationToken.None)).AiAssignments);
        fixture.Settings.Value.Ai.MaxConcurrentJobs = 1;

        var specification = await execution.GetExecutionAsync(firstNode.Id, first.JobId, first.Attempt, first.LeaseToken, CancellationToken.None);
        Assert.Equal("Running", specification.Execution.State);
        specification.Execution.State = "Partial";
        await execution.CompleteAsync(firstNode.Id, first.JobId, first.Attempt, first.LeaseToken,
            new() { Sequence = 1, Execution = specification.Execution }, CancellationToken.None);

        Assert.Empty((await scheduler.HeartbeatAsync(firstNode.Id, Heartbeat(firstNode), CancellationToken.None)).AiAssignments);
        Assert.Equal(1, await fixture.Jobs.CountDocumentsAsync(value => value.State == "Queued"));
        Assert.Equal(1, await fixture.Jobs.CountDocumentsAsync(value => value.State == "Running"));
    }

    /// <summary>可证明未 dispatch 的预授权在确认丢失后恢复序号和计数，不估计不存在的调用费用。</summary>
    [Fact]
    public async Task UnsentPreflightRecoversWithoutChargingOrLosingTheAcknowledgedSequence()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Workspace.Paths);
        var execution = Execution(fixture, workers);
        var scheduler = new AiJobScheduler(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, execution, new());
        var session = await fixture.SeedAsync();
        await fixture.Service().CreateAsync(session.Id, "user", false, CancellationToken.None);
        var node = await RegisterAsync(workers, fixture);
        var assignment = Assert.Single((await scheduler.HeartbeatAsync(node.Id, Heartbeat(node), CancellationToken.None)).AiAssignments);
        var specification = await execution.GetExecutionAsync(node.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken, CancellationToken.None);
        specification.Execution.RequestInFlight = true;
        specification.Execution.ModelRequests = 1;
        specification.Execution.ReservedTokens = 1200;
        await execution.CheckpointAsync(node.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken,
            new() { Sequence = 1, Execution = specification.Execution }, CancellationToken.None);
        await fixture.Jobs.UpdateOneAsync(value => value.Id == assignment.JobId,
            Builders<AiAnalysisJob>.Update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(-1)));
        await execution.MaintainAsync(CancellationToken.None);
        var nextToken = Guid.NewGuid().ToString("N");

        var recovery = await execution.RecoverAsync(node.Id,
            new() { JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken, NextLeaseToken = nextToken },
            new() { JobId = assignment.JobId, Attempt = assignment.Attempt, Sequence = 1, ModelRequests = 1, HasDispatchedRequest = false }, CancellationToken.None);

        Assert.Equal("Resume", recovery.Disposition);
        var resumed = await execution.GetExecutionAsync(node.Id, assignment.JobId, assignment.Attempt, nextToken, CancellationToken.None);
        Assert.Equal(1, resumed.CheckpointSequence);
        Assert.Equal(0, resumed.Execution.ModelRequests);
        Assert.False(resumed.Execution.RequestInFlight);
        Assert.Equal(0, resumed.Execution.EstimatedTokens);
        await Assert.ThrowsAsync<InfrastructureException>(() => execution.GetExecutionAsync(node.Id, assignment.JobId,
            assignment.Attempt, assignment.LeaseToken, CancellationToken.None));
    }

    /// <summary>已知响应的恢复只允许结算和回传，不能借恢复权限开始下一次模型调用。</summary>
    [Fact]
    public async Task KnownResponseRecoveryRejectsTheNextModelRequest()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Workspace.Paths);
        var execution = Execution(fixture, workers);
        var scheduler = new AiJobScheduler(fixture.Mongo, fixture.Settings, fixture.Workspace.Paths, workers, execution, new());
        var session = await fixture.SeedAsync();
        await fixture.Service().CreateAsync(session.Id, "user", false, CancellationToken.None);
        var node = await RegisterAsync(workers, fixture);
        var assignment = Assert.Single((await scheduler.HeartbeatAsync(node.Id, Heartbeat(node), CancellationToken.None)).AiAssignments);
        var specification = await execution.GetExecutionAsync(node.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken, CancellationToken.None);
        specification.Execution.RequestInFlight = true;
        specification.Execution.ModelRequests = 1;
        specification.Execution.ReservedTokens = 1200;
        await execution.CheckpointAsync(node.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken,
            new() { Sequence = 1, Execution = specification.Execution }, CancellationToken.None);
        var nextToken = Guid.NewGuid().ToString("N");
        var recovered = await execution.RecoverAsync(node.Id,
            new() { JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken, NextLeaseToken = nextToken },
            new() { JobId = assignment.JobId, Attempt = assignment.Attempt, Sequence = 1, ModelRequests = 1,
                HasDispatchedRequest = true, HasKnownResponse = true, RequestFingerprint = "known-response" }, CancellationToken.None);
        Assert.Equal("ReportOnly", recovered.Disposition);
        specification.Execution.ModelRequests = 2;
        await Assert.ThrowsAsync<InfrastructureException>(() => execution.CheckpointAsync(node.Id, assignment.JobId, assignment.Attempt,
            nextToken, new() { Sequence = 2, Execution = specification.Execution }, CancellationToken.None));
        specification.Execution.ModelRequests = 1;
        specification.Execution.RequestInFlight = false;
        specification.Execution.ReservedTokens = 0;
        specification.Execution.PromptTokens = 100;
        specification.Execution.CompletionTokens = 25;
        specification.Execution.EstimatedTokens = 0;

        await execution.CheckpointAsync(node.Id, assignment.JobId, assignment.Attempt, nextToken,
            new() { Sequence = 2, Execution = specification.Execution }, CancellationToken.None);
        var settled = await fixture.Jobs.Find(value => value.Id == assignment.JobId).SingleAsync();
        Assert.False(settled.UnsettledModelRequest);
        Assert.Equal(0, settled.EstimatedTokens);
        Assert.Equal(100, settled.PromptTokens);
    }

    private static async Task<TranscodeWorker> RegisterAsync(TranscodeWorkerService workers, AiMongoFixture fixture)
    {
        var created = await workers.CreateAsync("AI test", "AI", CancellationToken.None);
        await workers.ConnectAsync(new()
        {
            WorkerId = created.Worker.Id, Secret = created.Secret, Kind = "AI", ExpectedConfigurationRevision = created.Worker.ConfigurationRevision,
            InitialConfiguration = new() { TempDirectory = fixture.Workspace.Paths.DataDirectory, MinimumFreeBytes = 0, MaxConcurrentEncodes = 1 },
            Capabilities = new() { AiExecutionVersion = 1 }
        }, CancellationToken.None);
        return await workers.RequireAsync(created.Worker.Id, CancellationToken.None);
    }
}
