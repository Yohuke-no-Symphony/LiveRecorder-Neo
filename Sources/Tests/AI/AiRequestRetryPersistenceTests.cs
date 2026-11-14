using System.Text.Json.Nodes;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

[Trait("Category", "Integration")]
public sealed class AiRequestRetryPersistenceTests
{
    [Fact]
    public async Task RetryCountsAndEstimatedUsagePersistToTheJobAndPublicSummary()
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        var clock = new AiManualTimeProvider();
        var client = new AiTestChatClient((call, messages) => call == 1
            ? throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true)
            : Complete(messages));
        using var worker = fixture.Worker(new AiAnalysisRunner(client, fixture.Workspace.Data, clock));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var failedAttempt = await WaitForAccountedFailureAsync(fixture, summary.Id);
            Assert.Equal(1, failedAttempt.ModelRequests);
            Assert.Equal(0, failedAttempt.RequestRetries);
            Assert.Equal(0, failedAttempt.ReservedTokens);
            Assert.True(failedAttempt.EstimatedTokens > 0);
            var completed = await AdvanceUntilTerminalAsync(fixture, summary.Id, clock);
            Assert.Equal("Completed", completed.State);
            Assert.Equal(2, completed.ModelRequests);
            Assert.Equal(1, completed.RequestRetries);
            Assert.Equal(failedAttempt.EstimatedTokens, completed.EstimatedTokens);
            Assert.Equal(100, completed.PromptTokens);
            Assert.Equal(50, completed.CompletionTokens);
            var status = await fixture.Service().GetAsync(session.Id, true, CancellationToken.None);
            Assert.Equal(1, status.CurrentJob!.RequestRetries);
            Assert.Equal(1, Assert.Single(await fixture.Service().ListAsync(CancellationToken.None)).RequestRetries);
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("key")]
    [InlineData("retry")]
    [InlineData("timeout")]
    public async Task CancellationOrConfigurationChangesDuringBackoffPreventTheRetry(string change)
    {
        await using var fixture = await AiMongoFixture.CreateAsync();
        var session = await fixture.SeedAsync();
        var summary = await fixture.Service().CreateAsync(session.Id, "owner", false, CancellationToken.None);
        var clock = new AiManualTimeProvider();
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true));
        using var worker = fixture.Worker(new AiAnalysisRunner(client, fixture.Workspace.Data, clock));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var failedAttempt = await WaitForAccountedFailureAsync(fixture, summary.Id);
            switch (change)
            {
                case "cancel":
                    await fixture.Service().CancelAsync(summary.Id, CancellationToken.None);
                    break;
                case "key":
                    fixture.Settings.Value.Ai.ApiKey = fixture.Protector.Protect("rotated-test-secret");
                    break;
                case "retry":
                    fixture.Settings.Value.Ai.MaxRequestRetries = 0;
                    break;
                case "timeout":
                    fixture.Settings.Value.Ai.RequestTimeoutSeconds = 0;
                    break;
            }
            var stopped = await AdvanceUntilTerminalAsync(fixture, summary.Id, clock);
            Assert.Equal(change == "cancel" ? "Cancelled" : "Interrupted", stopped.State);
            if (change != "cancel")
            {
                Assert.Equal("AI_CONFIGURATION_CHANGED", stopped.ErrorCode);
            }
            Assert.Equal(1, client.Calls);
            Assert.Equal(1, stopped.ModelRequests);
            Assert.Equal(0, stopped.RequestRetries);
            Assert.Equal(failedAttempt.EstimatedTokens, stopped.EstimatedTokens);
            Assert.Equal(0, stopped.ReservedTokens);
            Assert.False(stopped.RequestInFlight);
            Assert.Null(stopped.ActiveKey);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<AiAnalysisJob> WaitForAccountedFailureAsync(AiMongoFixture fixture, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var job = await fixture.Jobs.Find(item => item.Id == id).SingleAsync(timeout.Token);
            if (job.ErrorCode == "AI_RESPONSE_INTERRUPTED" && !job.RequestInFlight)
            {
                return job;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<AiAnalysisJob> AdvanceUntilTerminalAsync(AiMongoFixture fixture, string id, AiManualTimeProvider clock)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var job = await fixture.Jobs.Find(item => item.Id == id).SingleAsync(timeout.Token);
            if (job.State is not ("Running" or "Queued"))
            {
                return job;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, timeout.Token);
        }
    }

    private static AiCompletion Complete(JsonArray messages)
    {
        var prompt = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
        return new()
        {
            PromptTokens = 100, CompletionTokens = 50,
            Message = new JsonObject
            {
                ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "finish", ["type"] = "function", ["function"] = new JsonObject
                    {
                        ["name"] = "submit_analysis", ["arguments"] = new JsonObject
                        {
                            ["overview"] = "已核对提供的消息，信息不足以提炼可靠话题。", ["complete"] = true,
                            ["reviewedEvidenceIds"] = prompt["segment"]!["requiredEvidenceIds"]!.DeepClone(), ["topics"] = new JsonArray()
                        }.ToJsonString()
                    }
                })
            }
        };
    }
}
