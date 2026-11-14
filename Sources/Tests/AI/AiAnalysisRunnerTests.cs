using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiAnalysisRunnerTests
{
    [Fact]
    public async Task ConfiguredModelRequestLimitStopsAfterOneRequest()
    {
        var job = Job();
        job.Settings.MaxModelRequests = 1;
        job.Settings.MaxToolCalls = 0;
        var client = new AiTestChatClient((_, _) => Tool("get_metrics", "{\"fromMs\":0,\"toMs\":60000}"));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal("AI_CALL_LIMIT_REACHED", job.ErrorCode);
    }

    [Fact]
    public async Task ConfiguredToolLimitBoundsMultipleCallsInOneModelResponse()
    {
        var job = Job();
        job.Settings.MaxToolCalls = 2;
        job.Settings.MaxModelRequests = 0;
        var response = Tool("get_metrics", "{\"fromMs\":0,\"toMs\":60000}");
        var calls = response.Message["tool_calls"]!.AsArray();
        calls.Add(calls[0]!.DeepClone());
        calls.Add(calls[0]!.DeepClone());
        var client = new AiTestChatClient((_, _) => response);
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, job.ToolCalls);
        Assert.Equal("AI_CALL_LIMIT_REACHED", job.ErrorCode);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1000000)]
    public async Task ConfiguredOutputLimitIsPassedToTheProviderWithoutClamping(int configuredLimit)
    {
        var job = Job();
        job.Settings.MaxOutputTokens = configuredLimit;
        var client = new AiTestChatClient((_, _) => Submit(true));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(configuredLimit, Assert.Single(client.OutputLimits));
        Assert.Equal("Completed", job.State);
    }

    [Fact]
    public async Task DeepSeekThinkingPreservesThreeRoundsOfReasoningAndTheUnbrokenToolChain()
    {
        var job = Job();
        job.Settings.Provider = "DeepSeek";
        job.Settings.ThinkingMode = "Enabled";
        var client = new AiTestChatClient((call, messages) =>
        {
            Assert.Equal(1, messages.Count(message => message?["role"]?.GetValue<string>() == "user"));
            var assistants = messages.Where(message => message?["role"]?.GetValue<string>() == "assistant").ToArray();
            Assert.Equal(call - 1, assistants.Length);
            for (var index = 0; index < assistants.Length; index++)
            {
                Assert.Equal("reasoning-" + (index + 1), assistants[index]!["reasoning_content"]!.GetValue<string>());
            }
            var response = call == 3 ? Submit(true) : Tool("get_metrics", "{\"fromMs\":0,\"toMs\":60000}");
            response.Message["reasoning_content"] = "reasoning-" + call;
            return response;
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(3, client.Calls);
        Assert.Equal("Completed", job.State);
    }

    [Fact]
    public async Task InitialSamplingUsesPopulatedMinutesAndDoesNotRepeatShortRecordingEvidence()
    {
        var job = Job();
        job.Snapshot.DurationMs = 36000000;
        job.Snapshot.Buckets = [new() { OffsetMs = 0, DurationMs = 60000, TranslationCount = 1 }];
        var client = new AiTestChatClient((_, messages) =>
        {
            var initial = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
            Assert.Single(initial["uniformlySampledEvidence"]!.AsArray());
            return Submit(true);
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
    }

    [Theory]
    [InlineData("get_messages", "{}")]
    [InlineData("search_messages", "{\"fromMs\":0,\"toMs\":60000}")]
    [InlineData("submit_analysis", "{}")]
    [InlineData("submit_analysis", "{\"overview\":\"x\",\"complete\":false,\"topics\":[{}]}")]
    public async Task MissingRequiredArgumentsAreRecoverableWithoutLosingTheJob(string name, string arguments)
    {
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 2)
            {
                Assert.Contains("Invalid tool", messages.ToJsonString());
            }
            return call == 1 ? Tool(name, arguments) : Submit(true);
        });
        var job = Job();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task FunctionCallsPersistEvidenceBeforeCompletingAndAccountUsage()
    {
        var checkpoints = new AiTestCheckpoint();
        var client = new AiTestChatClient((call, _) =>
        {
            Assert.True(checkpoints.Saved.Last().RequestInFlight);
            return call == 1 ? Tool("get_messages", """{"fromMs":0,"toMs":60000,"kind":"Translation"}""") : Submit(true);
        });
        var job = Job();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "secret", checkpoints, CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(2, job.ModelRequests);
        Assert.Equal(2, job.ToolCalls);
        Assert.Equal(200, job.PromptTokens);
        Assert.Equal(100, job.CompletionTokens);
        Assert.Equal(0, job.EstimatedTokens);
        Assert.Equal("0:1", Assert.Single(Assert.Single(job.Result.Topics).Evidence).Id);
        Assert.Contains(checkpoints.Saved, item => item.Result.Topics.Count == 1);
        Assert.All(client.OutputLimits, value => Assert.InRange(value, 1, 2048));
    }

    [Fact]
    public async Task UnknownEvidenceIsRejectedWithoutDiscardingLaterValidSubmission()
    {
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 2)
            {
                Assert.Contains("Invalid tool", messages.ToJsonString());
            }
            return call == 1 ? Submit(false, "fabricated") : Submit(true);
        });
        var job = Job();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Single(job.Result.Topics);
        Assert.DoesNotContain(job.Result.Topics.SelectMany(item => item.Evidence), item => item.Id == "fabricated");
    }

    [Fact]
    public async Task ReachingTheReferenceBudgetDoesNotPreventCompletionOrDiscardSavedTopics()
    {
        var job = Job();
        job.TokenBudget = 10;
        job.PromptTokens = 20;
        job.Result.Topics.Add(new() { Id = "saved", Title = "已保存" });
        var client = new AiTestChatClient((_, messages) =>
        {
            var overview = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
            Assert.Equal(10, overview["referenceBudget"]!.GetValue<int>());
            Assert.Equal(-10, overview["estimatedRemainingTokens"]!.GetValue<long>());
            Assert.Null(overview["remainingTokens"]);
            return Submit(true);
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal("Completed", job.State);
        Assert.Null(job.ErrorCode);
        Assert.Contains(job.Result.Topics, item => item.Id == "saved");
        Assert.Contains(job.Result.Topics, item => item.Id == "song");
    }

    [Fact]
    public async Task MissingUsageIsEstimatedWithoutStoppingAtTheReferenceBudget()
    {
        var client = new AiTestChatClient((call, _) => new()
        {
            Message = call == 1 ? Tool("get_metrics", """{"fromMs":0,"toMs":60000}""").Message : Submit(true).Message
        });
        var job = Job();
        job.TokenBudget = 10;
        var checkpoints = new AiTestCheckpoint();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", checkpoints, CancellationToken.None);
        Assert.True(job.EstimatedTokens > job.TokenBudget);
        Assert.Equal(0, job.PromptTokens + job.CompletionTokens);
        Assert.Equal("Completed", job.State);
        Assert.Equal(2, client.Calls);
        Assert.Equal(checkpoints.Saved.Where(item => item.RequestInFlight).Sum(item => item.ReservedTokens), job.EstimatedTokens);
    }

    [Fact]
    public async Task ZeroCallLimitsAllowCompletionBeyondTheFormerRequestAndToolLimits()
    {
        var job = Job();
        job.TokenBudget = 10;
        job.Settings.MaxModelRequests = 0;
        job.Settings.MaxToolCalls = 0;
        var client = new AiTestChatClient((call, _) =>
        {
            if (call == 25)
            {
                return Submit(true);
            }
            var response = Tool("get_metrics", $"{{\"fromMs\":{call * 100},\"toMs\":60000}}");
            var calls = response.Message["tool_calls"]!.AsArray();
            calls.Add(calls[0]!.DeepClone());
            return response;
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(25, job.ModelRequests);
        Assert.Equal(49, job.ToolCalls);
        Assert.True(job.PromptTokens + job.CompletionTokens > job.TokenBudget);
    }

    [Fact]
    public async Task UnknownOutcomeKeepsLargeInputAndOutputReservationWithoutIntegerOverflow()
    {
        var job = Job();
        job.Settings.MaxOutputTokens = int.MaxValue;
        var checkpoints = new AiTestCheckpoint();
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_NETWORK_UNKNOWN", true));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", checkpoints, CancellationToken.None);
        Assert.True(job.EstimatedTokens > int.MaxValue);
        Assert.Equal(Assert.Single(checkpoints.Saved, item => item.RequestInFlight).ReservedTokens, job.EstimatedTokens);
        Assert.Equal("Interrupted", job.State);
    }

    [Fact]
    public async Task UnknownNetworkOutcomeIsNotRetriedAndConsumesReservation()
    {
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_NETWORK_UNKNOWN", true));
        var job = Job();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal("Interrupted", job.State);
        Assert.True(job.EstimatedTokens > 0);
        Assert.False(job.RequestInFlight);
    }

    [Fact]
    public async Task RepeatedInvalidToolsCannotLoopIndefinitelyWithUnlimitedCalls()
    {
        var client = new AiTestChatClient((_, _) => Tool("unknown", "{}"));
        var job = Job();
        job.TokenBudget = 100000;
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(6, client.Calls);
        Assert.Equal(6, job.ToolCalls);
        Assert.Equal(1, job.Context.ProgressRecoveries);
        Assert.Equal(0, job.Context.ContextCompactions);
        Assert.Equal("AI_NO_PROGRESS", job.ErrorCode);
    }

    [Fact]
    public async Task PartialSubmissionSurvivesLaterModelFailure()
    {
        var client = new AiTestChatClient((call, _) => call == 1 ? Submit(false) : throw new AiProviderException("AI_HTTP_429"));
        var job = Job();
        var checkpoints = new AiTestCheckpoint();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", checkpoints, CancellationToken.None);
        Assert.Equal("Partial", job.State);
        Assert.Single(job.Result.Topics);
        Assert.False(job.Result.IsComplete);
        Assert.Equal(0, job.EstimatedTokens);
        Assert.Contains(checkpoints.Saved, item => item.Result.Topics.Count > 0 && item.ModelRequests == 1);
    }

    [Fact]
    public async Task CancelledRunCannotStartARequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new AiTestChatClient((_, _) => Submit(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiAnalysisRunner(client, new AiTestEvidenceReader())
            .RunAsync(Job(), "", new AiTestCheckpoint(), cancellation.Token));
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("AI_HTTP_400")]
    [InlineData("AI_HTTP_401")]
    public async Task ExplicitProviderRejectionDoesNotChargeAReservation(string code)
    {
        var client = new AiTestChatClient((_, _) => throw new AiProviderException(code));
        var job = Job();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, job.PromptTokens + job.CompletionTokens + job.EstimatedTokens);
        Assert.Equal(0, job.ReservedTokens);
        Assert.False(job.RequestInFlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderErrorsUseReturnedActualUsageInsteadOfTheReservation(bool unknown)
    {
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_OUTPUT_LIMIT_REACHED", unknown, 37, 19));
        var job = Job();
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(37, job.PromptTokens);
        Assert.Equal(19, job.CompletionTokens);
        Assert.Equal(0, job.EstimatedTokens);
    }

    [Fact]
    public async Task CancellationAtThePreflightCheckpointDoesNotInventAnIssuedRequest()
    {
        var client = new AiTestChatClient((_, _) => Submit(true));
        var job = Job();
        await Assert.ThrowsAsync<AiExecutionStoppedException>(() => new AiAnalysisRunner(client, new AiTestEvidenceReader())
            .RunAsync(job, "", new AiPreflightRejectingCheckpoint(), CancellationToken.None));
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, job.ModelRequests);
        Assert.Equal(0, job.ReservedTokens);
        Assert.Equal(0, job.EstimatedTokens);
        Assert.False(job.RequestInFlight);
    }

    [Theory]
    [InlineData(60000, 10000)]
    [InlineData(3600000, 20000)]
    [InlineData(36000000, 200000)]
    public void BudgetUsesTheProvidedRecordedDuration(double duration, int expected) => Assert.Equal(expected, AiAnalysisPolicy.Budget(new(), duration));

    private static AiAnalysisJob Job() => new()
    {
        State = "Running", TokenBudget = 20000, Settings = new() { Enabled = true, Model = "mock" },
        Snapshot = new() { SessionId = "s", Title = "直播", DurationMs = 60000, RecordedDurationMs = 60000, EvidenceCount = 1 },
        Result = new() { SessionId = "s" }
    };

    private static AiCompletion Submit(bool complete, string evidenceId = "0:1") => Tool("submit_analysis", $$"""
        {"overview":"依据同传观察到歌曲介绍，无法代表完整原声。","complete":{{complete.ToString().ToLowerInvariant()}},"topics":[{"id":"song","title":"歌曲介绍","summary":"介绍下一首歌曲","observations":"观众正在讨论歌曲；不推断因果。","startMs":0,"endMs":10000,"evidenceIds":["{{evidenceId}}"]}]}
        """);

    private static AiCompletion Tool(string name, string arguments) => new()
    {
        PromptTokens = 100, CompletionTokens = 50, FinishReason = "tool_calls",
        Message = new JsonObject
        {
            ["role"] = "assistant", ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "call", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
            })
        }
    };
}
