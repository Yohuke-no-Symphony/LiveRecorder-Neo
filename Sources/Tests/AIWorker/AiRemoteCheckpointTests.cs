using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.AI.Worker.Execution;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

public sealed class AiRemoteCheckpointTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-checkpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LostAcknowledgementRetransmitsOriginalPayloadBeforeLatestState()
    {
        var transfer = new AiTestTransport { FailCheckpointCount = 1 };
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var checkpoint = new AiRemoteCheckpoint(store, lease, transfer);
        var job = new AiAnalysisJob { Id = "job", Progress = 0.1 };

        await checkpoint.SaveAsync(job, default);
        job.Progress = 0.7;
        lease.Renew(90);
        await checkpoint.SaveAsync(job, default);

        Assert.Equal(new long[] { 1, 1, 2 }, transfer.Checkpoints.Select(item => item.Sequence));
        Assert.Equal(JsonSerializer.Serialize(transfer.Checkpoints[0]), JsonSerializer.Serialize(transfer.Checkpoints[1]));
        Assert.Equal(0.7, transfer.Checkpoints[2].Execution.Progress);
        Assert.Null(store.Read().Pending);
        Assert.Equal(2, store.Read().Sequence);
    }

    [Fact]
    public async Task ModelCallRequiresConfirmedCheckpointAndConnectedLease()
    {
        var transfer = new AiTestTransport { FailCheckpointCount = 1 };
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var checkpoint = new AiRemoteCheckpoint(store, lease, transfer);
        var job = new AiAnalysisJob { Id = "job", ModelRequests = 1, RequestInFlight = true };

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => checkpoint.SaveAsync(job, default));
        Assert.False(store.Read().Journal!.Dispatched);
        lease.Disconnect();
        await Assert.ThrowsAsync<AiExecutionPausedException>(() => checkpoint.SaveAsync(job, default));
        Assert.Single(transfer.Checkpoints);
    }

    [Fact]
    public async Task ResponseIsDurableBeforeReturningAndOfflinePureUpdatesDoNotDispatch()
    {
        var transfer = new AiTestTransport();
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var checkpoint = new AiRemoteCheckpoint(store, lease, transfer);
        var client = new AiTestChatClient();
        var journaled = new JournaledAiChatClient(client, store, lease);
        var job = new AiAnalysisJob { Id = "job", ModelRequests = 1, RequestInFlight = true };
        await checkpoint.SaveAsync(job, default);

        await journaled.CompleteAsync(job.Settings, "memory-only-key", new(), new(), 10, default);
        Assert.True(new AiCheckpointStore(directory, store.Read().Assignment).Read().Journal!.HasKnownResponse);
        lease.Disconnect();
        job.RequestInFlight = false;
        await checkpoint.SaveMemoryAsync(job, new() { ConversationJson = "[]" }, default);
        Assert.True(store.Read().Journal!.Applied);
        Assert.Equal(1, client.Calls);
        Assert.DoesNotContain("memory-only-key", File.ReadAllText(store.Path));
    }

    [Fact]
    public async Task ReportOnlyPermitsKnownResponseReplayButRejectsNextPaidCall()
    {
        var transfer = new AiTestTransport();
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var checkpoint = new AiRemoteCheckpoint(store, lease, transfer);
        var job = new AiAnalysisJob { Id = "job", ModelRequests = 1, RequestInFlight = true };
        await checkpoint.SaveAsync(job, default);
        var client = new AiTestChatClient();
        var first = new JournaledAiChatClient(client, store, lease);
        await first.CompleteAsync(job.Settings, "key", new(), new(), 10, default);

        lease.Renew(90, true);
        var recovered = checkpoint.PrepareRecovery(new() { Execution = AiExecutionMapper.ToExecution(job) });
        recovered.ModelRequests++;
        recovered.RequestInFlight = true;
        await checkpoint.SaveAsync(recovered, default);
        await new JournaledAiChatClient(client, store, lease).CompleteAsync(job.Settings, "key", new(), new(), 10, default);
        recovered.RequestInFlight = false;
        await checkpoint.SaveMemoryAsync(recovered, new(), default);
        recovered.RequestInFlight = true;
        recovered.ModelRequests++;

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => checkpoint.SaveAsync(recovered, default));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task DispatchedUnknownRequestCannotBeReplayedAfterRestart()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        store.Update(value => value.Journal = new() { RequestNumber = 1, Dispatched = true, RequestFingerprint = "unknown" });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var client = new AiTestChatClient();

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => new JournaledAiChatClient(client, store, lease)
            .CompleteAsync(new(), "key", new(), new(), 10, default));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task UnsentRecoveryAlignsServerSequenceAndPreservesLocalEvidencePath()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        store.Update(value =>
        {
            value.InputReady = true;
            value.Execution.Snapshot.EvidenceRelativePath = "evidence.jsonl";
            value.Execution.ModelRequests = 1;
            value.Journal = new() { RequestNumber = 1, Dispatched = false };
            value.Pending = new() { Sequence = 9, Execution = new() { JobId = "job", RequestInFlight = true, ModelRequests = 1 } };
            value.Sequence = 8;
        });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var transport = new AiTestTransport();
        var checkpoint = new AiRemoteCheckpoint(store, lease, transport);

        var recovered = checkpoint.PrepareRecovery(new() { CheckpointSequence = 9, Execution = new() { JobId = "job", ModelRequests = 0 } });
        await checkpoint.SaveAsync(recovered, default);

        Assert.Equal(0, recovered.ModelRequests);
        Assert.False(recovered.RequestInFlight);
        Assert.Equal("evidence.jsonl", recovered.Snapshot.EvidenceRelativePath);
        Assert.Equal(10, Assert.Single(transport.Checkpoints).Sequence);
        Assert.Empty(transport.Checkpoints[0].Execution.Snapshot.EvidenceRelativePath);
    }

    [Fact]
    public async Task FinalResultIsStoredWhileOfflineAndLostAcknowledgementReusesSamePayload()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        var lease = new AiExecutionLease();
        var transfer = new AiTestTransport { FailResultCount = 1 };
        var checkpoint = new AiRemoteCheckpoint(store, lease, transfer);
        var job = new AiAnalysisJob
        {
            Id = "job",
            SessionId = "session",
            State = "Partial",
            Result = new() { Overview = "saved" }
        };
        store.Update(value => value.Memory = new()
        {
            JobId = job.Id,
            SessionId = job.SessionId,
            Version = 7,
            ConversationJson = "[{\"role\":\"user\",\"content\":\"retained\"}]"
        });

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => checkpoint.CompleteAsync(job, null, default));
        var retained = Assert.IsType<AiAnalysisMemory>(new AiCheckpointStore(directory, store.Read().Assignment).Read().PendingResult?.Memory);
        Assert.Equal(job.Id, retained.JobId);
        Assert.Equal(job.SessionId, retained.SessionId);
        Assert.Equal(7, retained.Version);
        lease.Renew(90);
        await Assert.ThrowsAsync<IOException>(() => checkpoint.CompleteAsync(job, null, default));
        job.Result.Overview = "later mutation";
        store.Update(value =>
        {
            value.Memory.Version++;
            value.Memory.ConversationJson = "[]";
        });
        await checkpoint.CompleteAsync(job, null, default);

        Assert.Equal(2, transfer.Results.Count);
        Assert.Equal(JsonSerializer.Serialize(transfer.Results[0]), JsonSerializer.Serialize(transfer.Results[1]));
        Assert.Equal("saved", transfer.Results[1].Execution.Result.Overview);
        Assert.Equal(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(transfer.Results[1].Memory));
        Assert.Equal("Completed", store.Read().State);
    }

    [Fact]
    public async Task KnownResponseFingerprintMismatchNeverCallsProvider()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        store.Update(value => value.Journal = new()
        {
            Dispatched = true, RequestNumber = 1, RequestFingerprint = "different", Completion = new() { Message = new() { ["content"] = "known" } }
        });
        var lease = new AiExecutionLease();
        lease.Renew(90);
        var provider = new AiTestChatClient();

        await Assert.ThrowsAsync<AiExecutionPausedException>(() => new JournaledAiChatClient(provider, store, lease)
            .CompleteAsync(new(), "key", new(), new(), 10, default));

        Assert.Equal(0, provider.Calls);
        Assert.NotNull(store.Read().Journal!.Completion);
    }

    [Fact]
    public void FirstPreflightAcknowledgementLostWithoutJournalIsProvenUnsent()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        store.Update(value =>
        {
            value.Execution.ModelRequests = 1;
            value.Execution.RequestInFlight = true;
            value.Pending = new() { Sequence = 1, Execution = new() { JobId = "job", ModelRequests = 1, RequestInFlight = true } };
        });

        var proof = store.RecoveryCheckpoint();
        var recovered = new AiRemoteCheckpoint(store, new(), new AiTestTransport())
            .PrepareRecovery(new() { CheckpointSequence = 1, Execution = new() { JobId = "job", ModelRequests = 0 } });

        Assert.Equal(1, proof.ModelRequests);
        Assert.Equal(1, proof.Sequence);
        Assert.False(proof.HasDispatchedRequest);
        Assert.False(proof.HasKnownResponse);
        Assert.Equal(0, recovered.ModelRequests);
        Assert.False(recovered.RequestInFlight);
        Assert.Null(store.Read().Pending);
    }

    [Fact]
    public void SecondPreflightAcknowledgementLostCannotBorrowPreviousResponseProof()
    {
        var store = new AiCheckpointStore(directory, new() { JobId = "job", Attempt = 1 });
        store.Update(value =>
        {
            value.Sequence = 8;
            value.Execution.ModelRequests = 2;
            value.Execution.RequestInFlight = true;
            value.Pending = new() { Sequence = 9, Execution = new() { JobId = "job", ModelRequests = 2, RequestInFlight = true } };
            value.Journal = new()
            {
                RequestNumber = 1, RequestFingerprint = "previous", Dispatched = true, Applied = true,
                Completion = new() { Message = new() { ["content"] = "previous response" } }
            };
        });

        var proof = store.RecoveryCheckpoint();
        var recovered = new AiRemoteCheckpoint(store, new(), new AiTestTransport())
            .PrepareRecovery(new() { CheckpointSequence = 9, Execution = new() { JobId = "job", ModelRequests = 1 } });

        Assert.Equal(2, proof.ModelRequests);
        Assert.Equal(9, proof.Sequence);
        Assert.False(proof.HasDispatchedRequest);
        Assert.False(proof.HasKnownResponse);
        Assert.Null(proof.RequestFingerprint);
        Assert.Equal(1, recovered.ModelRequests);
        Assert.False(recovered.RequestInFlight);
        Assert.Null(store.Read().Pending);
        Assert.Null(store.Read().Journal);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
