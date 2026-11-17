using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal static class TranscodeContentIdentity
{
    internal static string Create(TranscodeAssignment assignment) => WorkerContentFingerprint.Create(new
    {
        PipelineVersion = 1, assignment.CheckpointGeneration, assignment.AssetId, assignment.SourceVersion,
        assignment.SourceLength, assignment.SourceETag, assignment.RecipeHash, assignment.Profile
    });
}
