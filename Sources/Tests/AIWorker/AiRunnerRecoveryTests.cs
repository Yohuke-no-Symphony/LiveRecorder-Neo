using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.AI.Worker.Execution;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

public sealed class AiRunnerRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-run-recovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CrashAfterResponseBeforeToolsReplaysKnownResponseWithoutChargingAgain()
    {
        var job = await CreateJobAsync();
        var store = new AiCheckpointStore(directory, new() { JobId = job.Id, Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var transfer = new AiTestTransport
        {
            OnCheckpoint = payload => payload.Execution.PromptTokens > 0 ? throw new WorkerLeaseLostException("Lost lease.") : Task.CompletedTask
        };
        var provider = new AiTestChatClient { Handler = () => Task.FromResult(Tool("submit_analysis", """{"overview":"有依据的话题","complete":true,"topics":[{"id":"topic","title":"话题","summary":"真实正文","observations":"","startMs":0,"endMs":2000,"evidenceIds":["0:1"]}]}""")) };
        var checkpoint = new AiRemoteCheckpoint(store, lease, transfer);

        await Assert.ThrowsAsync<WorkerLeaseLostException>(() => new AiAnalysisRunner(new JournaledAiChatClient(provider, store, lease), new AiFileEvidenceReader(directory))
            .RunAsync(job, "ephemeral-key", checkpoint, default));
        Assert.True(store.Read().Journal!.HasKnownResponse);
        Assert.False(store.Read().Journal!.Applied);

        var reopened = new AiCheckpointStore(directory, store.Read().Assignment);
        var reportingLease = new AiExecutionLease();
        reportingLease.Renew(90, true);
        var reportTransfer = new AiTestTransport();
        var recovery = new AiRemoteCheckpoint(reopened, reportingLease, reportTransfer);
        var recovered = recovery.PrepareRecovery(new() { Execution = store.Read().Journal!.BeforeExecution });
        await new AiAnalysisRunner(new JournaledAiChatClient(provider, reopened, reportingLease), new AiFileEvidenceReader(directory))
            .RunAsync(recovered, "ephemeral-key", recovery, default);
        await recovery.CompleteAsync(recovered, null, default);

        Assert.Equal("Completed", recovered.State);
        Assert.Single(recovered.Result.Topics);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, recovered.ModelRequests);
        Assert.Equal(1, recovered.ToolCalls);
        Assert.Equal(100, recovered.PromptTokens);
        Assert.Equal(50, recovered.CompletionTokens);
        Assert.DoesNotContain(reportTransfer.Checkpoints, payload => payload.Execution.RequestInFlight);
        Assert.Equal("Completed", reopened.Read().State);
        Assert.DoesNotContain("ephemeral-key", File.ReadAllText(reopened.Path));
    }

    [Fact]
    public async Task DisconnectAfterResponseAllowsPureToolsButStopsBeforeAnotherModelRequest()
    {
        var job = await CreateJobAsync();
        var store = new AiCheckpointStore(directory, new() { JobId = job.Id, Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var provider = new AiTestChatClient
        {
            Handler = () =>
            {
                lease.Disconnect();
                return Task.FromResult(Tool("get_metrics", """{"fromMs":0,"toMs":60000}"""));
            }
        };
        var checkpoint = new AiRemoteCheckpoint(store, lease, new AiTestTransport());

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => new AiAnalysisRunner(new JournaledAiChatClient(provider, store, lease), new AiFileEvidenceReader(directory))
            .RunAsync(job, "key", checkpoint, default));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, job.ModelRequests);
        Assert.Equal(1, job.ToolCalls);
        Assert.True(store.Read().Journal!.Applied);
        Assert.Contains("assistant", store.Read().Memory.ConversationJson);
        Assert.Contains("tool", store.Read().Memory.ConversationJson);
    }

    [Fact]
    public async Task UnknownProviderResultCannotStartAnotherPaidRetry()
    {
        var job = await CreateJobAsync();
        var store = new AiCheckpointStore(directory, new() { JobId = job.Id, Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var provider = new AiTestChatClient { Handler = () => throw new AiProviderException("AI_REQUEST_UNKNOWN", unknownOutcome: true, retryable: true) };
        var checkpoint = new AiRemoteCheckpoint(store, lease, new AiTestTransport());

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => new AiAnalysisRunner(new JournaledAiChatClient(provider, store, lease), new AiFileEvidenceReader(directory))
            .RunAsync(job, "key", checkpoint, default));

        Assert.Equal(1, provider.Calls);
        Assert.False(store.Read().Journal!.HasKnownResponse);
        Assert.True(store.Read().Journal!.Dispatched);
    }

    private async Task<AiAnalysisJob> CreateJobAsync()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.jsonl"), JsonSerializer.Serialize(new AiEvidence
        {
            Id = "0:1", OffsetMs = 1000, Kind = "Translation", Text = "有依据的直播正文"
        }) + "\n");
        return new()
        {
            Id = "job", SessionId = "session", State = "Running", Settings = new() { Enabled = true, Model = "model", MaxModelRequests = 10 },
            Snapshot = new()
            {
                SessionId = "session", DurationMs = 60000, EvidenceRelativePath = "evidence.jsonl", EvidenceCount = 1,
                Buckets = [new() { OffsetMs = 0, DurationMs = 60000, TranslationCount = 1 }],
                Segments = [new() { Index = 0, StartMs = 0, EndMs = 60000, EvidenceCount = 1 }]
            },
            Result = new() { JobId = "job", SessionId = "session" }
        };
    }

    private static AiCompletion Tool(string name, string arguments) => new()
    {
        PromptTokens = 100, CompletionTokens = 50, Message = new JsonObject
        {
            ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "call", ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
            })
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
