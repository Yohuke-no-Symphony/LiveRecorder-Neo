using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Provides committed FLV input plans through the application media boundary.</summary>
public sealed class IndexedFlvRangePlanner : IIndexedFlvRangePlanner
{
    /// <inheritdoc />
    public Task<IndexedFlvRangePlan> PlanAsync(string indexPath, long committedBytes, double startMs, double durationMs,
        CancellationToken cancellationToken = default) =>
        IndexedFlvRange.PlanAsync(indexPath, committedBytes, startMs, durationMs, cancellationToken);
}
