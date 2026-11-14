using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Application.AI;

namespace Yohuke.LiveRecorder.AI.Tests;

/// <summary>从模型消息、分段结果和已知使用量验证长期分析及上下文交接。</summary>
public sealed class AiLongAnalysisTests
{
    /// <summary>累计已返回使用量超过一百万后继续分析，反复压缩也不能破坏工具配对、证据和调用额度。</summary>
    [Theory]
    [InlineData("DeepSeek", "Enabled")]
    [InlineData("Custom", "Disabled")]
    public async Task CumulativeMillionTokenUsageSurvivesRepeatedCompaction(string provider, string thinkingMode)
    {
        var evidence = Enumerable.Range(0, 17).Select(index => Evidence("e-" + index, index * 2000 + 1000,
            $"消息{index}：" + new string('证', 360))).ToArray();
        var reader = new AiLongEvidenceReader(evidence);
        var job = Job(60000, evidence);
        job.Settings.Provider = provider;
        job.Settings.ThinkingMode = thinkingMode;
        job.Settings.ContextWindowTokens = 500000;
        job.Settings.MaxOutputTokens = 8192;
        var requests = new List<JsonArray>();
        var client = new AiTestChatClient((call, messages) =>
        {
            AssertProtocolComplete(messages, provider == "DeepSeek");
            requests.Add((JsonArray)messages.DeepClone());
            var response = call == 18 ? Submit(Prompt(messages), call, true) : Tool("get_messages", new()
            {
                ["fromMs"] = (call - 1) * 2000, ["toMs"] = call * 2000, ["kind"] = "All", ["limit"] = 40
            }, call);
            response.Message[provider == "DeepSeek" ? "reasoning_content" : "content"] = new string('x', 12000);
            return new AiCompletion
            {
                Message = response.Message, FinishReason = "tool_calls", PromptTokens = 100000 + messages.Count(item => item?["role"]?.GetValue<string>() == "assistant") * 50000, CompletionTokens = 8000
            };
        });

        await new AiAnalysisRunner(client, reader).RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.True(job.Result.IsComplete);
        Assert.Equal(18, client.Calls);
        Assert.True(job.PromptTokens > 1000000);
        Assert.Equal(144000, job.CompletionTokens);
        Assert.Equal(0, job.EstimatedTokens);
        Assert.True(job.Context.ContextCompactions >= 2);
        Assert.Equal(100, job.Progress);
        Assert.All(client.OutputLimits, limit => Assert.Equal(8192, limit));
        Assert.Contains(requests, messages => messages.Count(message => message?["role"]?.GetValue<string>() == "assistant") >= 2);
        Assert.True(requests.Count(messages => messages.All(message => message?["role"]?.GetValue<string>() != "assistant")) >= 3);
        Assert.Contains(Assert.Single(job.Result.Topics).Evidence, item => item.Id == "e-0");
    }

    /// <summary>中间无正文的段无需模型请求，独立上下文产生的相同局部话题 ID 不会覆盖前段。</summary>
    [Fact]
    public async Task EmptyMiddleSegmentAndRepeatedLocalTopicIdsRetainBothReviewedSegments()
    {
        var evidence = new[] { Evidence("first", 300000), Evidence("last", 3900000) };
        var job = Job(5400000, evidence);
        var client = new AiTestChatClient((call, messages) => Submit(Prompt(messages), call, true, "song"));

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal(2, client.Calls);
        Assert.Equal(new[] { "Completed", "Empty", "Completed" }, job.Context.Segments.Select(segment => segment.State));
        Assert.Equal(2, job.Result.Topics.Count);
        Assert.Equal(new[] { "song", "s2:song" }, job.Result.Topics.Select(topic => topic.Id));
        Assert.Equal(new[] { "first", "last" }, job.Result.Topics.SelectMany(topic => topic.Evidence).Select(item => item.Id));
        Assert.Equal(100, job.Progress);
    }

    /// <summary>只有寒暄的段可以确认已读而没有话题，不强迫模型编造内容来结束。</summary>
    [Fact]
    public async Task ReviewedEvidenceCanCompleteASegmentWithoutInventingATopic()
    {
        var evidence = new[] { Evidence("greeting", 1000, "哈哈哈") };
        var job = Job(60000, evidence);
        var client = new AiTestChatClient((call, messages) => Submit(Prompt(messages), call, true, noTopics: true));

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal("ReviewedNoTopics", Assert.Single(job.Context.Segments).State);
        Assert.True(job.Result.IsComplete);
        Assert.Empty(job.Result.Topics);
        Assert.False(string.IsNullOrWhiteSpace(job.Result.Overview));
    }

    /// <summary>首段的 complete 不能完成整场；后段结果未知时保留首段结果且不重发付费请求。</summary>
    [Fact]
    public async Task CompletingTheFirstSegmentDoesNotCompleteAnUnreviewedLaterSegment()
    {
        var evidence = new[] { Evidence("first", 1000), Evidence("second", 1801000) };
        var job = Job(3600000, evidence);
        var checkpoints = new AiTestCheckpoint();
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 1)
            {
                return Submit(Prompt(messages), call, true);
            }
            Assert.Equal("Completed", job.Context.Segments[0].State);
            Assert.False(job.Result.IsComplete);
            Assert.NotEqual("Completed", job.State);
            throw new AiProviderException("AI_NETWORK_UNKNOWN", true);
        });

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", checkpoints, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal("Interrupted", job.State);
        Assert.False(job.Result.IsComplete);
        Assert.Single(job.Result.Topics);
        Assert.NotEqual("Completed", job.Context.Segments[1].State);
        Assert.InRange(job.Progress, 0, 99.999);
        Assert.All(checkpoints.Saved.Where(saved => saved.Context.Segments.Count == 2 &&
            saved.Context.Segments[1].State is "Pending" or "Running"), saved => Assert.False(saved.Result.IsComplete));
    }

    /// <summary>明确拒绝的上下文请求可压缩后继续，网络结果未知则只保留一次预留估算并停止。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextRejectionCanRecoverButUnknownOutcomeCannotBeReplayed(bool unknownOutcome)
    {
        var evidence = new[] { Evidence("sample", 1000) };
        var job = Job(60000, evidence);
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call == 2)
            {
                throw new AiProviderException(unknownOutcome ? "AI_NETWORK_UNKNOWN" : "AI_CONTEXT_LENGTH_EXCEEDED", unknownOutcome);
            }
            return Submit(Prompt(messages), call, call > 1);
        });

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Single(job.Result.Topics);
        Assert.Equal(unknownOutcome ? 2 : 3, client.Calls);
        Assert.Equal(unknownOutcome ? "Interrupted" : "Completed", job.State);
        Assert.Equal(!unknownOutcome, job.Result.IsComplete);
        Assert.Equal(unknownOutcome ? 100 : 200, job.PromptTokens);
        Assert.Equal(unknownOutcome ? 50 : 100, job.CompletionTokens);
        if (unknownOutcome)
        {
            Assert.True(job.EstimatedTokens > 0);
        }
        else
        {
            Assert.Equal(0, job.EstimatedTokens);
            Assert.True(job.Context.ContextCompactions > 0);
        }
    }

    /// <summary>长录像超过八十个独立话题时，后续段仍可完成且不因展示数量限制丢失成果。</summary>
    [Fact]
    public async Task NinetyIndependentSegmentsDoNotHitTheFormerEightyTopicLimit()
    {
        var evidence = Enumerable.Range(0, 90).Select(index => Evidence("segment-" + index, index * 1800000d + 1000)).ToArray();
        var job = Job(90 * 1800000d, evidence);
        var client = new AiTestChatClient((call, messages) => Submit(Prompt(messages), call, true));

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal(90, client.Calls);
        Assert.Equal(90, job.Result.Topics.Count);
        Assert.Equal(90, job.Result.Topics.Select(topic => topic.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(90, job.Context.Segments.Count);
        Assert.All(job.Context.Segments, segment => Assert.Equal("Completed", segment.State));
        Assert.Equal(100, job.Progress);
    }

    /// <summary>相邻分段重述同一跨界事件时合并结果，并保留原始证据和两个段的完成状态。</summary>
    [Fact]
    public async Task AdjacentSegmentsMergeOverlappingTopicsOnlyWhenTheyShareEvidence()
    {
        var evidence = new[] { Evidence("before-boundary", 1799000), Evidence("after-boundary", 1800000) };
        var job = Job(3600000, evidence);
        var segmentCalls = new Dictionary<int, int>();
        var client = new AiTestChatClient((call, messages) =>
        {
            var prompt = Prompt(messages);
            var segment = prompt["segment"]!["index"]!.GetValue<int>();
            var turn = segmentCalls.GetValueOrDefault(segment) + 1;
            segmentCalls[segment] = turn;
            return turn == 1 ? Tool("get_messages", new()
            {
                ["fromMs"] = 1798000, ["toMs"] = 1802000, ["kind"] = "All", ["limit"] = 40
            }, call) : Submit(prompt, call, true, "bridge", evidence.Select(item => item.Id), 1798000, 1802000);
        });

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal(4, client.Calls);
        var topic = Assert.Single(job.Result.Topics);
        Assert.Equal(1798000, topic.StartMs);
        Assert.Equal(1802000, topic.EndMs);
        Assert.Equal(new[] { "before-boundary", "after-boundary" }, topic.Evidence.Select(item => item.Id));
        Assert.All(job.Context.Segments, segment => Assert.Equal("Completed", segment.State));
    }

    /// <summary>恢复读取断点时，分页不能把自身的分页日志追加到待遍历集合而永远无法结束。</summary>
    [Fact]
    public async Task ReadingSavedCursorsHasAFiniteEndAndDoesNotEnumerateItsOwnPagination()
    {
        var evidence = new[] { Evidence("one", 1000), Evidence("two", 3000), Evidence("three", 5000) };
        var job = Job(60000, evidence);
        job.Settings.MaxModelRequests = 20;
        var reads = new List<JsonObject>();
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call <= 3)
            {
                return Tool("get_messages", new()
                {
                    ["fromMs"] = (call - 1) * 2000, ["toMs"] = call * 2000, ["kind"] = "All", ["limit"] = 40
                }, call);
            }
            var cursor = "0";
            if (call > 4)
            {
                var response = JsonNode.Parse(messages.Last(message => message?["role"]?.GetValue<string>() == "tool")!
                    ["content"]!.GetValue<string>())!.AsObject();
                reads.AddRange(response["reads"]!.AsArray().Select(value => value!.AsObject()));
                if (response["nextCursor"] is null)
                {
                    return Submit(Prompt(messages), call, true);
                }
                cursor = response["nextCursor"]!.GetValue<string>();
            }
            return Tool("get_analysis_state", new() { ["kind"] = "Reads", ["cursor"] = cursor, ["limit"] = 1 }, call);
        });

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal(7, client.Calls);
        Assert.Equal(3, reads.Count);
        Assert.All(reads, read => Assert.Equal("get_messages", read["tool"]!.GetValue<string>()));
        Assert.Equal(new[] { 0d, 2000d, 4000d }, reads.Select(read => read["fromMs"]!.GetValue<double>()));
    }

    /// <summary>最后一分钟不足一分钟时，均匀抽样仍须读取尾桶，不能重复前一桶而遗漏末尾话题。</summary>
    [Fact]
    public async Task PartialFinalMinuteContributesItsOwnRequiredEvidence()
    {
        var evidence = new[] { Evidence("previous-minute", 59000), Evidence("final-minute", 60500) };
        var job = Job(61000, evidence);
        var client = new AiTestChatClient((call, messages) =>
        {
            var prompt = Prompt(messages);
            Assert.Equal(new[] { "previous-minute", "final-minute" }, prompt["segment"]!["requiredEvidenceIds"]!.AsArray()
                .Select(value => value!.GetValue<string>()));
            Assert.Contains(prompt["uniformlySampledEvidence"]!.AsArray(), value => value!["id"]!.GetValue<string>() == "final-minute");
            return Submit(prompt, call, true);
        });

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal(2, Assert.Single(job.Result.Topics).Evidence.Count);
    }

    /// <summary>全新压缩上下文可以重新读取先前状态页，不能把恢复记忆误判为连续无进展。</summary>
    [Fact]
    public async Task CompactedContextCanRestorePreviouslyVisitedStatePages()
    {
        var evidence = new[] { Evidence("one", 1000), Evidence("two", 3000), Evidence("three", 5000) };
        var job = Job(60000, evidence);
        job.Settings.ContextWindowTokens = 24000;
        job.Settings.MaxOutputTokens = 2048;
        job.Settings.MaxModelRequests = 12;
        var compactionsBeforeLongReply = 0;
        var client = new AiTestChatClient((call, messages) =>
        {
            if (call <= 3)
            {
                return Tool("get_messages", new()
                {
                    ["fromMs"] = (call - 1) * 2000, ["toMs"] = call * 2000, ["kind"] = "All", ["limit"] = 40
                }, call);
            }
            if (call == 7)
            {
                compactionsBeforeLongReply = job.Context.ContextCompactions;
                var response = Tool("get_metrics", new() { ["fromMs"] = 0, ["toMs"] = 60000 }, call);
                response.Message["content"] = new string('x', 180000);
                return new() { Message = response.Message, PromptTokens = AiAnalysisPolicy.EstimateTokens(messages.ToJsonString()), CompletionTokens = 50 };
            }
            if (call == 8)
            {
                Assert.True(job.Context.ContextCompactions > compactionsBeforeLongReply);
            }
            if (call is >= 4 and <= 6 or >= 8 and <= 10)
            {
                var page = call <= 6 ? call - 4 : call - 8;
                return Tool("get_analysis_state", new() { ["kind"] = "Reads", ["cursor"] = page.ToString(), ["limit"] = 1 }, call);
            }
            return Submit(Prompt(messages), call, true);
        });

        await new AiAnalysisRunner(client, new AiLongEvidenceReader(evidence))
            .RunAsync(job, "", new AiTestCheckpoint(), CancellationToken.None);

        Assert.Equal("Completed", job.State);
        Assert.Equal(11, client.Calls);
        Assert.Null(job.ErrorCode);
        Assert.True(job.Result.IsComplete);
    }

    private static AiAnalysisJob Job(double durationMs, IReadOnlyList<AiEvidence> evidence) => new()
    {
        State = "Running", TokenBudget = 10000,
        Settings = new()
        {
            Enabled = true, Provider = "Custom", Model = "test", ThinkingMode = "Disabled",
            ContextTokenTarget = 32000, MaxModelRequests = 0, MaxToolCalls = 0
        },
        Snapshot = new()
        {
            SessionId = "session", Title = "长直播复盘", DurationMs = durationMs, RecordedDurationMs = durationMs,
            EvidenceCount = evidence.Count,
            Buckets = evidence.GroupBy(item => Math.Floor(item.OffsetMs / 60000) * 60000).Select(group => new AiMetricBucket
            {
                OffsetMs = group.Key, DurationMs = Math.Min(60000, durationMs - group.Key), TranslationCount = group.Count()
            }).ToList()
        },
        Result = new() { SessionId = "session" }
    };

    private static AiEvidence Evidence(string id, double offsetMs, string text = "【正在介绍新的歌曲】") => new()
    {
        Id = id, OffsetMs = offsetMs, Kind = "Translation", Text = text
    };

    private static JsonObject Prompt(JsonArray messages) => JsonNode.Parse(messages.Last(message =>
        message?["role"]?.GetValue<string>() == "user")!["content"]!.GetValue<string>())!.AsObject();

    private static AiCompletion Submit(JsonObject prompt, int call, bool complete, string id = "topic",
        IEnumerable<string>? evidenceIds = null, double? fromMs = null, double? toMs = null, bool noTopics = false)
    {
        var segment = prompt["segment"]!.AsObject();
        var required = segment["requiredEvidenceIds"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.NotEmpty(required);
        var ids = (evidenceIds ?? required).Distinct(StringComparer.Ordinal).ToArray();
        var topics = new JsonArray();
        if (!noTopics)
        {
            topics.Add(new JsonObject
            {
                ["id"] = id, ["title"] = "歌曲介绍", ["summary"] = "由收到的同传消息支持的话题。", ["observations"] = "不推断因果。",
                ["startMs"] = fromMs ?? segment["startMs"]!.GetValue<double>(),
                ["endMs"] = toMs ?? segment["endMs"]!.GetValue<double>(),
                ["evidenceIds"] = new JsonArray(ids.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            });
        }
        return Tool("submit_analysis", new()
        {
            ["overview"] = noTopics ? "已核对消息，仅有寒暄，无法支持具体话题。" : "基于同传和观众消息的分段复盘，不能代表完整原声。",
            ["complete"] = complete, ["topics"] = topics,
            ["reviewedEvidenceIds"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        }, call);
    }

    private static AiCompletion Tool(string name, JsonObject arguments, int call) => new()
    {
        PromptTokens = 100, CompletionTokens = 50, FinishReason = "tool_calls",
        Message = new JsonObject
        {
            ["role"] = "assistant", ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "call-" + call, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() }
            })
        }
    };

    private static void AssertProtocolComplete(JsonArray messages, bool requireReasoning)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message?["role"]?.GetValue<string>() == "assistant" && message["tool_calls"] is JsonArray calls)
            {
                Assert.Empty(pending);
                if (requireReasoning)
                {
                    Assert.False(string.IsNullOrEmpty(message["reasoning_content"]?.GetValue<string>()));
                }
                foreach (var call in calls)
                {
                    Assert.True(pending.Add(call!["id"]!.GetValue<string>()));
                }
            }
            else if (message?["role"]?.GetValue<string>() == "tool")
            {
                Assert.True(pending.Remove(message["tool_call_id"]!.GetValue<string>()));
            }
        }
        Assert.Empty(pending);
    }
}
