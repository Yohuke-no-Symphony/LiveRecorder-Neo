using System.Diagnostics;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Clipping.Worker.Scheduling;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipStageContext(WorkerCheckpointLocation<ClipAssignment, ClipCheckpointPayload> location,
    ClipWorkspaceLease files, WorkerConfiguration configuration, ClipFileTransfer transfers, ClipRunningJob job,
    Action<string> log, bool supportsRecovery)
    : WorkerStageContext(location.Checkpoint.Stages,
        () => WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>.Save(location),
        (definition, stage, stages) =>
        {
            job.Update(definition.Id, definition.StartProgress + (definition.EndProgress - definition.StartProgress) * stage.Progress / 100, stage.Activity);
            job.SetStages(stages);
        })
{
    private long transferStarted;
    private long initialBytes;
    internal ClipAssignment Assignment => location.Checkpoint.Assignment;
    internal ClipCheckpointPayload Payload => location.Checkpoint.Payload;
    internal ClipWorkspaceLease Files { get; } = files;
    internal WorkerConfiguration Configuration { get; } = configuration;
    internal ClipFileTransfer Transfers { get; } = transfers;
    internal ClipExecutor Executor { get; } = new(transfers);
    internal Action<string> Log { get; } = log;
    internal bool SupportsRecovery { get; } = supportsRecovery;

    internal void BeginTransfer()
    {
        transferStarted = 0;
        initialBytes = 0;
    }

    internal void Transfer(long completed, long total)
    {
        if (transferStarted == 0)
        {
            transferStarted = Stopwatch.GetTimestamp();
            initialBytes = completed;
        }
        var elapsed = Stopwatch.GetElapsedTime(transferStarted).TotalSeconds;
        var speed = elapsed >= 1 ? Math.Max(0, completed - initialBytes) / elapsed : (double?)null;
        Report(total > 0 ? completed * 100d / total : 0, new()
        {
            BytesTransferred = completed, TotalBytes = total, BytesPerSecond = speed,
            EstimatedSecondsRemaining = speed is > 0 ? Math.Max(0, total - completed) / speed : null
        });
    }

    internal void ExportProgress(double fraction) => Report(fraction * 100, new()
    {
        ProcessedDurationMs = fraction * (Assignment.ActualEndMs - Assignment.ActualStartMs),
        TotalDurationMs = Assignment.ActualEndMs - Assignment.ActualStartMs
    });
}
