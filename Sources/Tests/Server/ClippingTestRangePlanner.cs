using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class ClippingTestRangePlanner : IIndexedFlvRangePlanner
{
    internal bool Available { get; set; } = true;
    internal long LastCommittedBytes { get; private set; }
    internal int HeaderBytes { get; set; } = 3;
    internal bool BlockPlans { get; set; }
    internal TaskCompletionSource PlanEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ContinuePlan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IndexedFlvRangePlan> PlanAsync(string indexPath, long committedBytes, double startMs, double durationMs,
        CancellationToken cancellationToken = default)
    {
        LastCommittedBytes = committedBytes;
        if (BlockPlans)
        {
            PlanEntered.TrySetResult();
            await ContinuePlan.Task.WaitAsync(cancellationToken);
        }
        if (!Available)
        {
            throw new InvalidOperationException("Legacy source has no usable index.");
        }
        return new IndexedFlvRangePlan
        {
            SourceLength = committedBytes, StartOffset = 0, EndOffset = committedBytes,
            StartMs = 0, EndMs = 1000, VideoHeader = new byte[HeaderBytes]
        };
    }
}
