using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiRequestRetryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(37L, 19L)]
    [InlineData(37L, null)]
    public async Task InterruptedResponseRetriesTheSameRequestAndAccountsEachAttemptOnce(long? prompt, long? completion)
    {
        var job = Job();
        var clock = new AiManualTimeProvider();
        var checkpoint = new AiTestCheckpoint();
        string? request = null;
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 1)
            {
                request = messages.ToJsonString();
                throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, prompt, completion, true);
            }
            Assert.Equal(request, messages.ToJsonString());
            return Submit(messages);
        });
        await DriveAsync(new AiAnalysisRunner(client, new AiTestEvidenceReader(), clock).RunAsync(job, "", checkpoint, CancellationToken.None), clock);
        Assert.Equal("Completed", job.State);
        Assert.Equal(2, job.ModelRequests);
        Assert.Equal(1, job.RequestRetries);
        Assert.Equal(1, job.ToolCalls);
        Assert.Equal(100 + (prompt ?? 0), job.PromptTokens);
        Assert.Equal(50 + (completion ?? 0), job.CompletionTokens);
        var reservation = checkpoint.Saved.First(saved => saved.RequestInFlight).ReservedTokens;
        Assert.Equal((prompt is null ? reservation - job.Settings.MaxOutputTokens : 0) + (completion is null ? job.Settings.MaxOutputTokens : 0), job.EstimatedTokens);
        Assert.Contains(checkpoint.Memories, _ => checkpoint.Saved.Any(saved => saved.ErrorCode == "AI_RESPONSE_INTERRUPTED" && !saved.RequestInFlight && saved.ReservedTokens == 0));
        Assert.Equal(0, job.Context.ProgressRecoveries);
        Assert.Equal(0, job.ReservedTokens);
        Assert.False(job.RequestInFlight);
        Assert.Null(job.ErrorCode);
    }

    [Fact]
    public async Task RetryExhaustionPreservesAllAttemptEstimatesAndStops()
    {
        var job = Job();
        var checkpoint = new AiTestCheckpoint();
        var clock = new AiManualTimeProvider();
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true));
        await DriveAsync(new AiAnalysisRunner(client, new AiTestEvidenceReader(), clock).RunAsync(job, "", checkpoint, CancellationToken.None), clock);
        Assert.Equal(3, client.Calls);
        Assert.Equal(3, job.ModelRequests);
        Assert.Equal(2, job.RequestRetries);
        Assert.Equal("Interrupted", job.State);
        Assert.Equal("AI_RESPONSE_INTERRUPTED", job.ErrorCode);
        Assert.Equal(checkpoint.Saved.Where(saved => saved.RequestInFlight).Sum(saved => saved.ReservedTokens), job.EstimatedTokens);
        Assert.Equal(0, job.ReservedTokens);
        Assert.False(job.RequestInFlight);
        Assert.Equal(0, job.ToolCalls);
    }

    [Theory]
    [InlineData("AI_HTTP_403", false, 2)]
    [InlineData("AI_OUTPUT_LIMIT_REACHED", false, 2)]
    [InlineData("AI_RESPONSE_INTERRUPTED", true, 0)]
    public async Task PermanentErrorsOrDisabledRetryNeverIssueAnotherRequest(string code, bool retryable, int retries)
    {
        var job = Job();
        job.Settings.MaxRequestRetries = retries;
        var client = new AiTestChatClient((_, _) => throw new AiProviderException(code, retryable, retryable: retryable));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, job.RequestRetries);
        Assert.Equal(code, job.ErrorCode);
    }

    [Fact]
    public async Task GlobalRequestLimitPreventsAConfiguredRetry()
    {
        var job = Job();
        job.Settings.MaxModelRequests = 1;
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, job.RequestRetries);
        Assert.Equal("AI_CALL_LIMIT_REACHED", job.ErrorCode);
        Assert.Equal("Partial", job.State);
        Assert.True(job.EstimatedTokens > 0);
    }

    [Fact]
    public async Task SuccessResetsTheRetryAllowanceForTheNextLogicalRequest()
    {
        var job = Job();
        job.Settings.MaxRequestRetries = 1;
        var clock = new AiManualTimeProvider();
        var client = new AiTestChatClient((call, messages) => call switch
        {
            1 => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true),
            2 => Tool("get_metrics", "{\"fromMs\":0,\"toMs\":60000}"),
            3 => throw new AiProviderException("AI_HTTP_503", retryable: true),
            _ => Submit(messages)
        });
        await DriveAsync(new AiAnalysisRunner(client, new AiTestEvidenceReader(), clock).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None), clock);
        Assert.Equal("Completed", job.State);
        Assert.Equal(4, job.ModelRequests);
        Assert.Equal(2, job.RequestRetries);
        Assert.Equal(2, job.ToolCalls);
        Assert.Equal(200, job.PromptTokens);
        Assert.Equal(100, job.CompletionTokens);
        Assert.Equal(0, job.Context.ProgressRecoveries);
    }

    [Fact]
    public async Task CancellationDuringBackoffDoesNotIssueOrReserveARetry()
    {
        var job = Job();
        var clock = new AiManualTimeProvider();
        var checkpoint = new AiTestCheckpoint();
        using var cancellation = new CancellationTokenSource();
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true));
        var run = new AiAnalysisRunner(client, new AiTestEvidenceReader(), clock).RunAsync(job, "", checkpoint, cancellation.Token);
        Assert.Equal(1, client.Calls);
        Assert.False(checkpoint.Saved.Last().RequestInFlight);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, job.ModelRequests);
        Assert.Equal(0, job.RequestRetries);
        Assert.Equal(0, job.ReservedTokens);
        Assert.True(job.EstimatedTokens > 0);
    }

    [Fact]
    public async Task RetryPreflightFailureRollsBackBothDispatchCounters()
    {
        var job = Job();
        var clock = new AiManualTimeProvider();
        var checkpoint = new AiRetryRejectingCheckpoint();
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true));
        var error = await Assert.ThrowsAsync<AiExecutionStoppedException>(() => DriveAsync(
            new AiAnalysisRunner(client, new AiTestEvidenceReader(), clock).RunAsync(job, "", checkpoint, CancellationToken.None), clock));
        Assert.Equal("AI_CONFIGURATION_CHANGED", error.Code);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, job.ModelRequests);
        Assert.Equal(0, job.RequestRetries);
        Assert.Equal(0, job.ReservedTokens);
        Assert.False(job.RequestInFlight);
        Assert.True(job.EstimatedTokens > 0);
    }

    [Fact]
    public async Task FailureAccountingMustBePersistedBeforeAnotherAttempt()
    {
        var job = Job();
        var clock = new AiManualTimeProvider();
        var client = new AiTestChatClient((_, _) => throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true));
        await Assert.ThrowsAsync<IOException>(() => new AiAnalysisRunner(client, new AiTestEvidenceReader(), clock)
            .RunAsync(job, "", new AiRetryRejectingCheckpoint(true), CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, job.RequestRetries);
        Assert.False(job.RequestInFlight);
        Assert.True(job.EstimatedTokens > 0);
    }

    [Fact]
    public async Task RetryingALaterSegmentDoesNotReanalyzeOrDiscardCompletedSegments()
    {
        var job = Job();
        job.Snapshot.DurationMs = 3600000;
        var evidence = new[]
        {
            new AiEvidence { Id = "first", OffsetMs = 1000, Kind = "Translation", Text = "第一段内容" },
            new AiEvidence { Id = "second", OffsetMs = 1801000, Kind = "Translation", Text = "第二段内容" }
        };
        var clock = new AiManualTimeProvider();
        string? failedRequest = null;
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 2)
            {
                Assert.Equal("Completed", job.Context.Segments[0].State);
                Assert.Single(job.Result.Topics);
                failedRequest = messages.ToJsonString();
                throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true);
            }
            if (call == 3)
            {
                Assert.Equal(failedRequest, messages.ToJsonString());
            }
            return Submit(messages);
        });
        await DriveAsync(new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence), clock).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None), clock);
        Assert.Equal("Completed", job.State);
        Assert.Equal(3, client.Calls);
        Assert.Equal(1, job.RequestRetries);
        Assert.Equal(2, job.Result.Topics.Count);
        Assert.All(job.Context.Segments, segment => Assert.Equal("Completed", segment.State));
        Assert.Equal(new[] { "first", "second" }, job.Result.Topics.SelectMany(topic => topic.Evidence).Select(item => item.Id));
    }

    private static async Task DriveAsync(Task run, AiManualTimeProvider clock)
    {
        for (var tick = 0; tick < 500 && !run.IsCompleted; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static AiAnalysisJob Job() => new()
    {
        State = "Running", Settings = new() { Enabled = true, Model = "mock" },
        Snapshot = new() { DurationMs = 60000, RecordedDurationMs = 60000, EvidenceCount = 1 }
    };

    private static AiCompletion Submit(JsonArray messages)
    {
        var prompt = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
        var segment = prompt["segment"]!;
        return Tool("submit_analysis", new JsonObject
        {
            ["overview"] = "依据已提供证据的分段复盘。", ["complete"] = true,
            ["topics"] = new JsonArray(new JsonObject
            {
                ["id"] = "topic", ["title"] = "歌曲讨论", ["summary"] = "观众正在讨论歌曲。", ["observations"] = "不推断原声或因果。",
                ["startMs"] = segment["startMs"]!.DeepClone(), ["endMs"] = segment["endMs"]!.DeepClone(),
                ["evidenceIds"] = segment["requiredEvidenceIds"]!.DeepClone()
            })
        }.ToJsonString());
    }

    private static AiCompletion Tool(string name, string arguments) => new()
    {
        PromptTokens = 100, CompletionTokens = 50,
        Message = new JsonObject
        {
            ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "call", ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
            })
        }
    };
}
