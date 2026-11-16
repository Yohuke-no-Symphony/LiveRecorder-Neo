using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

internal sealed class ClipPlanPreparationEntry(string key, MediaJob job)
{
    internal string Key { get; } = key;
    internal MediaJob Job { get; } = job;
    internal DateTime? CompletedAtUtc { get; set; }
    internal TaskCompletionSource<ClipJobPlan?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
