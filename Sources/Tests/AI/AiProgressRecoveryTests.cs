using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiProgressRecoveryTests
{
    [Fact]
    public async Task AFirstMalformedSubmissionAfterRepeatedReadsGetsARepairOpportunity()
    {
        var job = Job();
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call <= 3)
            {
                return Read(0, 60000);
            }
            if (call == 4)
            {
                return Tool("submit_analysis", "{\"overview\":\"unescaped \"quote\" text\",\"complete\":true,\"topics\":[]}");
            }
            var error = JsonNode.Parse(messages.Last()!["content"]!.GetValue<string>())!;
            Assert.Equal("json_syntax_error", error["type"]!.GetValue<string>());
            Assert.True(error["bytePositionInLine"]!.GetValue<long>() > 0);
            return Submit();
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(5, client.Calls);
        Assert.Equal(0, job.Context.ProgressRecoveries);
    }

    [Fact]
    public async Task RepeatedReadsRecoverWithSpecificSubmissionInstructionsAndKeepTheSavedTopic()
    {
        var job = Job();
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 1)
            {
                return Submit(false);
            }
            if (job.Context.ProgressRecoveries == 0)
            {
                return Read(0, 60000);
            }
            var prompt = JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
            Assert.NotNull(prompt["recovery"]);
            Assert.Single(job.Result.Topics);
            Assert.Equal(2, messages.Count);
            return Submit();
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(1, job.Context.ProgressRecoveries);
        Assert.Equal(0, job.Context.ContextCompactions);
        Assert.Single(job.Result.Topics);
    }

    [Fact]
    public async Task ANewContextCanRestoreThreeOldReadWindowsBeforeCorrectingMalformedJson()
    {
        var evidence = Enumerable.Range(0, 3).Select(index => new AiEvidence
        {
            Id = "e" + index, OffsetMs = index * 2000 + 1000, Kind = "Translation", Text = "收到的消息"
        }).ToArray();
        var job = Job();
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 4)
            {
                throw new AiProviderException("AI_CONTEXT_LENGTH_EXCEEDED");
            }
            if (call <= 3 || call is >= 5 and <= 7)
            {
                var index = call <= 3 ? call - 1 : call - 5;
                var response = Read(index * 2000, (index + 1) * 2000);
                if (call == 3)
                {
                    response.Message["content"] = new string('x', 50000);
                }
                return response;
            }
            if (call == 8)
            {
                Assert.Equal(0, job.Context.ProgressRecoveries);
                return Tool("submit_analysis", "{\"overview\":\"bad \"quotes\"\"}");
            }
            Assert.Contains("json_syntax_error", messages.Last()!["content"]!.GetValue<string>());
            return Tool("submit_analysis", """{"overview":"已核对三段消息，只能确认观众讨论。","complete":true,"reviewedEvidenceIds":["e0","e1","e2"],"topics":[]}""");
        });
        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence)).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(9, client.Calls);
        Assert.Equal(1, job.Context.ContextCompactions);
        Assert.Equal(0, job.Context.ProgressRecoveries);
    }

    [Fact]
    public async Task RepeatedCompressionCannotTurnTheSameReadIntoEndlessProgress()
    {
        var job = Job();
        job.Settings.ContextWindowTokens = 10000;
        var client = new AiTestChatClient((call, messages) =>
        {
            Assert.True(call < 20, "The repeated read escaped the progress guard.");
            var response = Read(0, 60000);
            response.Message["content"] = new string('x', 90000);
            return new()
            {
                Message = response.Message, PromptTokens = AiAnalysisPolicy.EstimateTokens(messages.ToJsonString()), CompletionTokens = 50
            };
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("AI_NO_PROGRESS", job.ErrorCode);
        Assert.True(job.Context.ContextCompactions >= 2);
        Assert.Equal(1, job.Context.ProgressRecoveries);
        Assert.InRange(client.Calls, 6, 12);
    }

    [Fact]
    public async Task AutomaticCapacityDoesNotCompressAtTheOldThreshold()
    {
        var job = Job();
        job.Settings.ContextTokenTarget = 8192;
        job.Settings.ContextWindowTokens = 0;
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 1)
            {
                var response = Read(0, 60000);
                response.Message["content"] = new string('x', 90000);
                return response;
            }
            Assert.Contains(messages, message => message?["role"]?.GetValue<string>() == "assistant");
            return Submit();
        });
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal(0, job.Context.ContextCompactions);
    }

    [Fact]
    public async Task AlternatingPageSizesCannotResetTheCrossContextProgressGuard()
    {
        var evidence = Enumerable.Range(0, 40).Select(index => new AiEvidence
        {
            Id = "e" + index, OffsetMs = index * 1000 + 1, Kind = "Translation", Text = "已知消息"
        }).ToArray();
        var job = Job();
        job.Settings.ContextWindowTokens = 10000;
        var client = new AiTestChatClient((call, messages) =>
        {
            Assert.True(call < 25, "Alternating existing page sizes escaped the progress guard.");
            var response = Tool("get_messages", new JsonObject
            {
                ["fromMs"] = 0, ["toMs"] = 60000, ["kind"] = "All", ["limit"] = call % 2 == 1 ? 40 : 1
            }.ToJsonString());
            response.Message["content"] = new string('x', 90000);
            return new() { Message = response.Message, PromptTokens = AiAnalysisPolicy.EstimateTokens(messages.ToJsonString()), CompletionTokens = 50 };
        });
        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence)).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("AI_NO_PROGRESS", job.ErrorCode);
        Assert.Equal(1, job.Context.ProgressRecoveries);
        Assert.InRange(client.Calls, 6, 16);
    }

    [Fact]
    public async Task SegmentZeroAcceptsTheDocumentedPrefix()
    {
        var job = Job();
        var client = new AiTestChatClient((_, _) => Submit(id: "s0:topic"));
        await new AiAnalysisRunner(client, new AiTestEvidenceReader()).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);
        Assert.Equal("Completed", job.State);
        Assert.Equal("topic", Assert.Single(job.Result.Topics).Id);
    }

    private static AiAnalysisJob Job() => new()
    {
        State = "Running", Settings = new() { Enabled = true, Model = "mock" },
        Snapshot = new() { DurationMs = 60000, RecordedDurationMs = 60000, EvidenceCount = 1 }
    };

    private static AiCompletion Read(double from, double to) => Tool("get_messages", new JsonObject
    {
        ["fromMs"] = from, ["toMs"] = to, ["kind"] = "All", ["limit"] = 40
    }.ToJsonString());

    private static AiCompletion Submit(bool complete = true, string id = "topic") => Tool("submit_analysis", new JsonObject
    {
        ["overview"] = "依据已提供同传的复盘。", ["complete"] = complete,
        ["topics"] = new JsonArray(new JsonObject
        {
            ["id"] = id, ["title"] = "歌曲讨论", ["summary"] = "消息讨论歌曲。", ["observations"] = "不能推断因果。",
            ["startMs"] = 0, ["endMs"] = 60000, ["evidenceIds"] = new JsonArray("0:1")
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
