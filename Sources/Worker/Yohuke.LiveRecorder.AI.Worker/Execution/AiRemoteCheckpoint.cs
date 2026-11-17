using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.AI.Worker.Transport;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Execution;

internal sealed class AiRemoteCheckpoint(AiCheckpointStore store, AiExecutionLease lease, IAiExecutionTransport transport) : IAiRunCheckpoint
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private int? replayRequest;
    private bool replayProcessing;

    internal AiAnalysisJob PrepareRecovery(AiExecutionSpecification specification)
    {
        var local = store.Read();
        var recovery = store.RecoveryCheckpoint();
        var requestJournal = local.Journal?.RequestNumber == recovery.ModelRequests ? local.Journal : null;
        if (requestJournal is { Applied: false, HasKnownResponse: true } journal)
        {
            replayRequest = journal.RequestNumber;
            replayProcessing = true;
            var execution = AiCheckpointStore.Clone(journal.BeforeExecution);
            execution.ModelRequests = Math.Max(0, execution.ModelRequests - 1);
            execution.RequestInFlight = false;
            execution.ReservedTokens = 0;
            store.Update(value =>
            {
                value.Memory = AiCheckpointStore.Clone(journal.BeforeMemory);
                value.Execution = execution;
                value.Journal!.Returned = false;
            });
            return AiExecutionMapper.ToJob(execution);
        }
        if (requestJournal is { Dispatched: true, HasKnownResponse: false })
        {
            throw new AiExecutionPausedException("The result of the dispatched AI request is unknown.");
        }
        if (!recovery.HasDispatchedRequest && (requestJournal is not null || local.Execution.RequestInFlight || local.Pending?.Execution.RequestInFlight == true))
        {
            store.Update(value =>
            {
                value.Execution = AiCheckpointStore.Clone(specification.Execution);
                if (value.InputReady)
                {
                    value.Execution.Snapshot = AiCheckpointStore.Clone(local.Execution.Snapshot);
                }
                value.Memory = AiCheckpointStore.Clone(specification.Memory ?? value.Memory);
                value.Sequence = specification.CheckpointSequence;
                value.Pending = null;
                value.Journal = null;
            });
        }
        return AiExecutionMapper.ToJob(store.Read().Execution);
    }

    /// <inheritdoc />
    public Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken) => SaveCoreAsync(job, null, cancellationToken);

    /// <inheritdoc />
    public Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory memory, CancellationToken cancellationToken) => SaveCoreAsync(job, memory, cancellationToken);

    /// <inheritdoc />
    public Task<AiAnalysisMemory> LoadMemoryAsync(AiAnalysisJob job, CancellationToken cancellationToken) => Task.FromResult(store.Read().Memory);

    private async Task SaveCoreAsync(AiAnalysisJob job, AiAnalysisMemory? memory, CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try
        {
            var replay = job.RequestInFlight && replayRequest == job.ModelRequests;
            if (job.RequestInFlight && !replay)
            {
                lease.RequireModelCall();
                if (store.Read().Journal is { Dispatched: true } previous && (!previous.HasKnownResponse || !previous.Applied))
                {
                    throw new AiExecutionPausedException("An earlier AI request has an unknown result.");
                }
            }
            store.Update(value =>
            {
                value.Execution = LocalExecution(job);
                if (memory is not null)
                {
                    value.Memory = AiCheckpointStore.Clone(memory);
                    if (!job.RequestInFlight && value.Journal is { Returned: true })
                    {
                        value.Journal.Applied = true;
                    }
                }
                if (job.RequestInFlight && !replay)
                {
                    value.Journal = new()
                    {
                        RequestNumber = job.ModelRequests, BeforeExecution = LocalExecution(job),
                        BeforeMemory = AiCheckpointStore.Clone(value.Memory)
                    };
                }
            });
            if (replay)
            {
                replayRequest = null;
                return;
            }
            if (replayProcessing)
            {
                if (memory is null || store.Read().Journal is not { Returned: true, Applied: true })
                {
                    return;
                }
                replayProcessing = false;
            }
            if (store.Read().Journal is { Dispatched: true, HasKnownResponse: false })
            {
                return;
            }
            if (!lease.CanReport)
            {
                return;
            }
            try
            {
                await FlushCurrentAsync(cancellationToken);
                if (job.RequestInFlight)
                {
                    store.Update(value => value.Journal!.Confirmed = true);
                }
            }
            catch (Exception error) when (IsTransient(error, cancellationToken))
            {
                lease.Disconnect();
                if (job.RequestInFlight)
                {
                    throw new AiExecutionPausedException();
                }
            }
        }
        finally
        {
            writes.Release();
        }
    }

    internal async Task FlushCurrentAsync(CancellationToken cancellationToken)
    {
        if (!lease.CanReport)
        {
            throw new AiExecutionPausedException();
        }
        var current = store.Read();
        if (current.Pending is not null)
        {
            await SendPendingAsync(current, cancellationToken);
        }
        current = store.Read();
        store.Update(value => value.Pending = new()
        {
            Sequence = value.Sequence + 1, Execution = WireExecution(value.Execution), Memory = AiCheckpointStore.Clone(value.Memory)
        });
        await SendPendingAsync(store.Read(), cancellationToken);
    }

    private async Task SendPendingAsync(AiWorkerCheckpoint current, CancellationToken cancellationToken)
    {
        var payload = current.Pending!;
        var ack = await transport.CheckpointAsync(current.Assignment, payload, cancellationToken);
        if (ack.Sequence != payload.Sequence)
        {
            throw new InvalidDataException("The coordinator acknowledged a different AI checkpoint sequence.");
        }
        store.Update(value =>
        {
            value.Sequence = ack.Sequence;
            value.Pending = null;
        });
    }

    internal async Task CompleteAsync(AiAnalysisJob job, AiConnectionTestOutcome? connectionTest, CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try
        {
            store.Update(value => value.Execution = LocalExecution(job));
            var current = store.Read();
            if (current.PendingResult is null)
            {
                store.Update(value =>
                {
                    value.State = "Reporting";
                    value.PendingResult = new()
                    {
                        Sequence = Math.Max(value.Sequence, value.Pending?.Sequence ?? 0) + 1, Execution = WireExecution(value.Execution),
                        Memory = AiCheckpointStore.Clone(value.Memory), ConnectionTest = connectionTest,
                        Stages = value.Stages.Select(stage => stage with
                        {
                            State = stage.State == "Pending" ? "Skipped" : stage.State == "Running" ? "Completed" : stage.State,
                            Progress = stage.State == "Running" ? 100 : stage.Progress,
                            FinishedAtUtc = stage.FinishedAtUtc ?? DateTime.UtcNow
                        }).ToList()
                    };
                });
            }
            if (!lease.CanReport)
            {
                throw new AiExecutionPausedException();
            }
            current = store.Read();
            if (current.Pending is not null)
            {
                await SendPendingAsync(current, cancellationToken);
                current = store.Read();
            }
            var ack = await transport.CompleteAsync(current.Assignment, current.PendingResult!, cancellationToken);
            if (ack.Sequence != current.PendingResult!.Sequence)
            {
                throw new InvalidDataException("The coordinator acknowledged a different AI result sequence.");
            }
            store.Update(value =>
            {
                value.Sequence = ack.Sequence;
                value.PendingResult = null;
                value.State = "Completed";
            });
        }
        finally
        {
            writes.Release();
        }
    }

    private static bool IsTransient(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error is not WorkerLeaseLostException &&
        error is HttpRequestException or IOException or OperationCanceledException;

    private static AiExecutionState LocalExecution(AiAnalysisJob job)
    {
        var execution = AiExecutionMapper.ToExecution(job);
        execution.Snapshot.EvidenceRelativePath = job.Snapshot.EvidenceRelativePath;
        return execution;
    }

    private static AiExecutionState WireExecution(AiExecutionState execution) => AiExecutionMapper.ToExecution(AiExecutionMapper.ToJob(execution));
}
