using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal static class ClipContentIdentity
{
    internal static string Create(ClipAssignment assignment) => WorkerContentFingerprint.Create(new
    {
        PipelineVersion = 1, assignment.CheckpointGeneration, assignment.Mode, assignment.StartMs, assignment.EndMs,
        assignment.ActualStartMs, assignment.ActualEndMs, assignment.Inputs
    });
}
