using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

internal sealed class StageTestContext(List<WorkerStageSnapshot> stages, Action persist)
    : WorkerStageContext(stages, persist, (_, _, _) => { })
{
    internal Dictionary<string, int> Executions { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> ValidArtifacts { get; } = new(StringComparer.Ordinal);
}
