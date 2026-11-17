using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.AI.Worker.Storage;

internal sealed class AiCheckpointStore
{
    private readonly Lock sync = new();
    private AiWorkerCheckpoint current;
    internal string Path { get; }

    internal AiCheckpointStore(string directory, AiAssignment assignment)
    {
        Path = System.IO.Path.Combine(directory, "checkpoint.json");
        if (File.Exists(Path))
        {
            current = WorkerJsonFileStore.Read<AiWorkerCheckpoint>(Path);
            if (current.Assignment.JobId != assignment.JobId || current.Assignment.Attempt != assignment.Attempt ||
                current.Assignment.InputFingerprint != assignment.InputFingerprint || current.Assignment.ConfigurationFingerprint != assignment.ConfigurationFingerprint)
            {
                throw new InvalidDataException("The AI checkpoint belongs to a different execution.");
            }
        }
        else
        {
            current = new() { Assignment = Clone(assignment), Execution = new() { JobId = assignment.JobId } };
            WorkerJsonFileStore.Write(Path, current);
        }
    }

    internal AiWorkerCheckpoint Read()
    {
        lock (sync)
        {
            return Clone(current);
        }
    }

    internal void Update(Action<AiWorkerCheckpoint> update)
    {
        lock (sync)
        {
            var next = Clone(current);
            update(next);
            next.LastAccessAtUtc = DateTime.UtcNow;
            next.Execution.Context.ConversationPath = null;
            if (next.Pending is not null)
            {
                next.Pending.Execution.Context.ConversationPath = null;
            }
            WorkerJsonFileStore.Write(Path, next);
            current = next;
        }
    }

    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions), WorkerProtocol.JsonOptions)!;

    internal AiRecoveryCheckpoint RecoveryCheckpoint()
    {
        var snapshot = Read();
        var requestNumber = Math.Max(snapshot.Execution.ModelRequests,
            Math.Max(snapshot.Pending?.Execution.ModelRequests ?? 0, snapshot.PendingResult?.Execution.ModelRequests ?? 0));
        var journal = snapshot.Journal?.RequestNumber == requestNumber ? snapshot.Journal : null;
        return new()
        {
            JobId = snapshot.Assignment.JobId, Attempt = snapshot.Assignment.Attempt,
            Sequence = Math.Max(snapshot.Sequence, Math.Max(snapshot.Pending?.Sequence ?? 0, snapshot.PendingResult?.Sequence ?? 0)),
            ModelRequests = requestNumber, HasKnownResponse = journal?.HasKnownResponse == true,
            HasDispatchedRequest = journal?.Dispatched == true, RequestFingerprint = journal?.RequestFingerprint
        };
    }
}
