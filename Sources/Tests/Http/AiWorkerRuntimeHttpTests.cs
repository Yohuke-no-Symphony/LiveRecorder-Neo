using System.Net;
using Xunit;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实控制连接和主站 HTTP 校验 AI 节点准备失败、缓存压力下的暂停与恢复。</summary>
public sealed class AiWorkerRuntimeHttpTests
{
    /// <summary>尚无私有上下文的准备失败可以完成上报，并释放唯一并发槽执行连接测试。</summary>
    [MongoHttpFact]
    public Task PreparationFailureIsAcknowledgedAndWorkerCanRunConnectionTest() => RunAsync(false);

    /// <summary>准备失败保留主站下发的已有上下文身份、版本和正文。</summary>
    [MongoHttpFact]
    public Task PreparationFailurePreservesExistingMemory() => RunAsync(true);

    /// <summary>损坏检查点与不足预算使节点暂停派单，保留未知请求并在预算恢复后继续处理新任务。</summary>
    [MongoHttpFact]
    public async Task UnreclaimableWorkspaceReportsConfigurationErrorAndResumesAfterBudgetIncrease()
    {
        await using var fixture = await AiWorkerRuntimeHttpFixture.CreateAsync();
        fixture.SeedBlockedCache();
        var blockedRevision = await fixture.SetCacheBudgetAsync(1024 * 1024);
        var queued = await fixture.QueueAnalysisAsync(withEvidence: false);
        fixture.StartWorker(truncateSource: false);
        await fixture.WaitUntilAsync(async () => (await fixture.ReadWorkerAsync()).ConfigurationError is not null);
        var blocked = await fixture.ReadWorkerAsync();
        Assert.Equal(blockedRevision, blocked.ConfigurationRevision);
        Assert.NotEqual(blockedRevision, blocked.AppliedConfigurationRevision);
        Assert.Equal(0, blocked.Capabilities.AiExecutionVersion);
        Assert.NotNull(blocked.LastSeenAtUtc);
        var firstSeen = blocked.LastSeenAtUtc.Value;
        await fixture.WaitUntilAsync(async () => (await fixture.ReadWorkerAsync()).LastSeenAtUtc is { } seen && seen > firstSeen);
        var second = await fixture.ReadWorkerAsync();
        Assert.NotNull(second.ConfigurationError);
        var secondSeen = second.LastSeenAtUtc!.Value;
        await fixture.WaitUntilAsync(async () => (await fixture.ReadWorkerAsync()).LastSeenAtUtc is { } seen && seen > secondSeen);
        Assert.NotNull((await fixture.ReadWorkerAsync()).ConfigurationError);
        var waiting = await fixture.ReadJobAsync(queued.Id);
        Assert.Equal("Queued", waiting.State);
        Assert.Equal(0, waiting.Attempt);
        Assert.Null(waiting.WorkerId);
        Assert.Null(waiting.LeaseOwner);
        Assert.Null(waiting.LeaseUntilUtc);
        Assert.False(Directory.Exists(fixture.CompletedCacheDirectory));
        foreach (var file in fixture.ProtectedCacheFiles)
        {
            Assert.True(File.Exists(file.Key), file.Key);
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key));
        }
        Assert.Empty(fixture.ProviderRequests);

        var restoredRevision = await fixture.SetCacheBudgetAsync(16L * 1024 * 1024);

        await fixture.WaitUntilAsync(async () =>
        {
            var worker = await fixture.ReadWorkerAsync();
            return worker.ConfigurationError is null && worker.AppliedConfigurationRevision == restoredRevision;
        });
        Assert.Equal(1, (await fixture.ReadWorkerAsync()).Capabilities.AiExecutionVersion);
        await fixture.WaitUntilAsync(async () => (await fixture.ReadJobAsync(queued.Id)).State == "Partial");
        var completed = await fixture.ReadJobAsync(queued.Id);
        Assert.Equal("AI_NO_EVIDENCE", completed.ErrorCode);
        Assert.Equal(fixture.WorkerId, completed.WorkerId);
        Assert.Equal(1, completed.Attempt);
        Assert.Equal(0, completed.ModelRequests);
        Assert.Null(completed.ActiveKey);
        Assert.Null(completed.LeaseUntilUtc);
        await fixture.WaitUntilAsync(() => Task.FromResult(fixture.Completions.Any(value => value.Result.Execution.JobId == queued.Id)));
        var completion = Assert.Single(fixture.Completions, value => value.Result.Execution.JobId == queued.Id);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        Assert.Equal("Partial", completion.Result.Execution.State);
        Assert.Empty(fixture.ProviderRequests);
    }

    private static async Task RunAsync(bool hasMemory)
    {
        await using var fixture = await AiWorkerRuntimeHttpFixture.CreateAsync();
        var queued = await fixture.QueueAnalysisAsync();
        AiAnalysisMemory? expectedMemory = null;
        if (hasMemory)
        {
            expectedMemory = await fixture.SeedMemoryAsync(queued);
        }
        fixture.StartWorker();
        await fixture.WaitUntilAsync(() => Task.FromResult(fixture.Completions.Any(value => value.Result.Execution.JobId == queued.Id)));
        var completion = Assert.Single(fixture.Completions, value => value.Result.Execution.JobId == queued.Id);
        Assert.True(completion.StatusCode == HttpStatusCode.OK,
            $"Preparation failure was rejected with {(int)completion.StatusCode}. Memory identity was '{completion.Result.Memory?.JobId}'/'{completion.Result.Memory?.SessionId}'.\n{completion.Body}");
        Assert.Equal("Failed", completion.Result.Execution.State);
        Assert.Equal("AI_SOURCE_UNAVAILABLE", completion.Result.Execution.ErrorCode);
        Assert.NotNull(completion.Result.Memory);
        Assert.Equal(queued.Id, completion.Result.Memory.JobId);
        Assert.Equal(queued.SessionId, completion.Result.Memory.SessionId);
        if (expectedMemory is not null)
        {
            Assert.Equal(expectedMemory.Version, completion.Result.Memory.Version);
            Assert.Equal(expectedMemory.ConversationJson, completion.Result.Memory.ConversationJson);
        }
        var failed = await fixture.ReadJobAsync(queued.Id);
        Assert.Equal("Failed", failed.State);
        Assert.Equal("AI_SOURCE_UNAVAILABLE", failed.ErrorCode);
        Assert.Equal(fixture.WorkerId, failed.WorkerId);
        Assert.Equal(0, failed.ModelRequests);
        Assert.Null(failed.ActiveKey);
        Assert.Null(failed.LeaseOwner);
        Assert.Null(failed.LeaseUntilUtc);
        Assert.NotNull(failed.FinishedAtUtc);
        Assert.True(fixture.OriginalSourceLength > fixture.DeliveredSourceLength);
        Assert.Empty(fixture.ProviderRequests);
        await fixture.WaitUntilAsync(() => Task.FromResult(fixture.ReadCheckpoint(queued.Id)?.State == "Completed"));
        Assert.Null(fixture.ReadCheckpoint(queued.Id)!.PendingResult);

        var connected = await fixture.TestConnectionAsync();

        Assert.True(connected.Success);
        Assert.Equal(12, connected.PromptTokens);
        Assert.Equal(8, connected.CompletionTokens);
        var providerRequest = Assert.Single(fixture.ProviderRequests);
        Assert.Equal("connection_test", providerRequest["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Failed", (await fixture.ReadJobAsync(queued.Id)).State);
    }
}
