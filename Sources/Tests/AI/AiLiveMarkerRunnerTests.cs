using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiLiveMarkerRunnerTests
{
    [Fact]
    public async Task DedicatedGenerationRejectsAnalysisSubmissionAndUsesTheActualAnchorTime()
    {
        var job = Job();
        var client = new AiTestChatClient((call, messages) =>
        {
            Assert.Contains("直播记录整理员", messages[0]!["content"]!.GetValue<string>());
            if (call == 1)
            {
                return Tool("submit_analysis", "{\"overview\":\"wrong purpose\",\"complete\":true,\"topics\":[]}");
            }
            Assert.Contains("Invalid tool", messages.ToJsonString());
            return Submit(true, "a", "观众讨论下一首歌");
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(AiJobPurposes.LIVE_MARKERS, job.Result.Purpose);
        Assert.Empty(job.Result.Topics);
        var marker = Assert.Single(job.Result.LiveMarkers);
        Assert.Equal(1000, marker.OffsetMs);
        Assert.Equal("0:1", marker.AnchorEvidenceId);
        Assert.Null(job.Result.LiveMarkerFileId);
    }

    [Fact]
    public async Task IncrementalMarkersSurviveCompactionAndCanBeRestoredAndRevisedWithoutDuplication()
    {
        var job = Job();
        var checkpoint = new AiTestCheckpoint();
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 1)
            {
                var response = Submit(false, "a", "先保存的记录");
                response.Message["content"] = new string('x', 50000);
                return response;
            }
            if (call == 2)
            {
                throw new AiProviderException("AI_CONTEXT_LENGTH_EXCEEDED");
            }
            if (call == 3)
            {
                var prompt = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
                Assert.Equal("先保存的记录", prompt["handoff"]!["markers"]![0]!["message"]!.GetValue<string>());
                Assert.Equal(2, messages.Count);
                return Tool("get_analysis_state", "{\"kind\":\"LiveMarkers\",\"limit\":1}");
            }
            var restored = JsonNode.Parse(messages.Last()!["content"]!.GetValue<string>())!;
            Assert.Equal("先保存的记录", restored["markers"]![0]!["message"]!.GetValue<string>());
            return Submit(true, "a", "经过核对的记录");
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", checkpoint, CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(1, job.Context.ContextCompactions);
        Assert.Equal(0, job.Context.ProgressRecoveries);
        Assert.Equal("经过核对的记录", Assert.Single(job.Result.LiveMarkers).Message);
        Assert.Contains(checkpoint.Saved, saved => saved.Result.LiveMarkers.Any(marker => marker.Message == "先保存的记录"));
    }

    [Fact]
    public async Task RevisingAnExistingIdIntoADuplicateRemovesTheOldEntry()
    {
        var job = Job();
        var client = new AiTestChatClient((call, _) => call switch
        {
            1 => Submit(false, "a", "旧记录"),
            2 => Submit(false, "b", "保留记录"),
            _ => Submit(true, "a", "保留记录")
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        var marker = Assert.Single(job.Result.LiveMarkers);
        Assert.Equal("s0:b", marker.Id);
        Assert.Equal("保留记录", marker.Message);
        Assert.Equal("Completed", job.State);
    }

    [Fact]
    public async Task AnalysisCannotSubmitMarkersAndInventedAnchorCannotBePublished()
    {
        var job = Job();
        job.Settings.MaxModelRequests = 1;
        var client = new AiTestChatClient((_, _) => Tool("submit_live_markers", """
            {"overview":"无证据","complete":true,"markers":[{"id":"bad","message":"凭空事件","anchorEvidenceId":"invented","evidenceIds":["invented"]}]}
            """));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Empty(job.Result.LiveMarkers);
        Assert.False(job.Result.IsComplete);
        job = Job();
        job.Purpose = AiJobPurposes.ANALYSIS;
        job.Settings.MaxModelRequests = 1;
        await new AiAnalysisRunner(new AiTestChatClient((_, _) => Submit(true, "a", "合法证据但错误用途")), new AiTestEvidenceReader())
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Empty(job.Result.LiveMarkers);
        Assert.False(job.Result.IsComplete);
    }

    private static AiAnalysisJob Job() => new()
    {
        Purpose = AiJobPurposes.LIVE_MARKERS, State = "Running", TokenBudget = 20000,
        Snapshot = new() { DurationMs = 60000, RecordedDurationMs = 60000, EvidenceCount = 1 },
        Settings = new() { Enabled = true, Model = "mock" }
    };

    private static AiCompletion Submit(bool complete, string id, string message) => Tool("submit_live_markers", new JsonObject
    {
        ["overview"] = "依据消息生成的直播记录。", ["complete"] = complete,
        ["markers"] = new JsonArray(new JsonObject
        {
            ["id"] = id, ["message"] = message, ["anchorEvidenceId"] = "0:1", ["evidenceIds"] = new JsonArray("0:1")
        })
    }.ToJsonString());

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
