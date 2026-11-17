namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal sealed record WorkerCheckpointLocation<TAssignment, TPayload>(string Path, WorkerJobCheckpoint<TAssignment, TPayload> Checkpoint)
    where TAssignment : new()
    where TPayload : new();
