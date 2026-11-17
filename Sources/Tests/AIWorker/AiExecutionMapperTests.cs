using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

/// <summary>验证节点执行描述不泄露凭据、主站私有路径或共享可变实例。</summary>
public sealed class AiExecutionMapperTests
{
    /// <summary>传输状态只含执行参数，修改传输结果不改变主站持有的结果。</summary>
    [Fact]
    public void TransportRemovesCredentialsAndPrivateContextPaths()
    {
        var job = new AiAnalysisJob
        {
            Settings = new() { Enabled = true, Model = "model", ApiKey = "provider-secret", HasApiKey = true },
            Context = new() { ConversationPath = "/private/server/ai/context.json", ConversationVersion = 3 },
            Snapshot = new() { EvidenceRelativePath = "ai/snapshots/private-legacy.jsonl" },
            Result = new() { Overview = "original" }
        };

        var execution = AiExecutionMapper.ToExecution(job);
        var serialized = JsonSerializer.Serialize(execution);
        execution.Result.Overview = "changed";

        Assert.DoesNotContain("provider-secret", serialized);
        Assert.DoesNotContain("/private/server", serialized);
        Assert.Null(execution.Context.ConversationPath);
        Assert.Empty(execution.Snapshot.EvidenceRelativePath);
        Assert.DoesNotContain("private-legacy", serialized);
        Assert.Equal("original", job.Result.Overview);
        Assert.Equal("model", execution.Settings.Model);
    }

    /// <summary>恢复保持用量和在途边界，不把节点状态误变为新的任务。</summary>
    [Fact]
    public void RoundTripPreservesExecutionAndOutstandingRequestAccounting()
    {
        var original = new AiExecutionState
        {
            JobId = "job", SessionId = "session", Purpose = AiJobPurposes.LIVE_MARKERS,
            State = "Running", Progress = 35, TokenBudget = 1000, PromptTokens = 11, CompletionTokens = 12,
            EstimatedTokens = 13, ReservedTokens = 14, ModelRequests = 5, RequestRetries = 2, ToolCalls = 7,
            RequestInFlight = true, Settings = new() { Model = "model", MaxRequestRetries = 3 },
            Context = new() { ConversationVersion = 4 }
        };

        var job = AiExecutionMapper.ToJob(original);
        var restored = AiExecutionMapper.ToExecution(job);

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
        Assert.True(job.Settings.Enabled);
        Assert.Equal("", job.Settings.ApiKey);
        Assert.Null(job.LeaseOwner);
        Assert.NotSame(original.Context, job.Context);
    }
}
