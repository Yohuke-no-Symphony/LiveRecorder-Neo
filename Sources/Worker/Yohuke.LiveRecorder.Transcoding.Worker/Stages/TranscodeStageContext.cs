using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeStageContext(
    WorkerCheckpointLocation<TranscodeAssignment, TranscodeCheckpointPayload> location,
    WorkerWorkspaceLease files, WorkerConfiguration configuration, WorkerFileTransfer transfers,
    MediaInspector inspector, TranscodeEncoder encoder, WorkerRunningJob job, WorkerJobReporter reporter,
    Func<CancellationToken, Task<(WorkerRuntimeConfiguration Configuration, EncodingReservation Reservation)>> reserve,
    bool supportsRecovery)
    : WorkerStageContext(location.Checkpoint.Stages,
        () => WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>.Save(location),
        (definition, stage, stages) =>
        {
            job.Update(definition.Id, definition.StartProgress + (definition.EndProgress - definition.StartProgress) * stage.Progress / 100,
                location.Checkpoint.Payload.Encoder, stage.Activity);
            job.SetStages(stages);
        })
{
    internal TranscodeAssignment Assignment => location.Checkpoint.Assignment;
    internal TranscodeCheckpointPayload Payload => location.Checkpoint.Payload;
    internal WorkerWorkspaceLease Files { get; } = files;
    internal WorkerConfiguration Configuration { get; set; } = configuration;
    internal WorkerFileTransfer Transfers { get; } = transfers;
    internal MediaInspector Inspector { get; } = inspector;
    internal TranscodeEncoder Encoder { get; } = encoder;
    internal WorkerJobReporter Reporter { get; } = reporter;
    internal bool SupportsRecovery { get; } = supportsRecovery;
    internal Func<CancellationToken, Task<(WorkerRuntimeConfiguration Configuration, EncodingReservation Reservation)>> Reserve { get; } = reserve;

    internal string OutputDirectory => WorkerArtifactStore.Resolve(Files.JobDirectory, Payload.OutputDirectory!);

    internal void Transfer(WorkerTransferProgress progress)
    {
        Reporter.Transfer(progress);
        Report(progress.TotalBytes > 0 ? progress.BytesTransferred * 100d / progress.TotalBytes : 0,
            job.Snapshot().Activity);
    }

    internal void EncodingProgress(Execution.WorkerEncodingProgress progress)
    {
        Reporter.Encoding(progress, Payload.Source!.DurationMs);
        Report(Payload.Source.DurationMs > 0 ? progress.ProcessedDurationMs * 100d / Payload.Source.DurationMs : 0,
            job.Snapshot().Activity);
    }
}
