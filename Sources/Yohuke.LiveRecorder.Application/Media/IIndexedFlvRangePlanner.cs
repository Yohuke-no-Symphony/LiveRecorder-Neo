namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>Plans the independently decodable FLV bytes within a committed recording snapshot.</summary>
public interface IIndexedFlvRangePlanner
{
    /// <summary>Resolves a keyframe-aligned range without reading unrelated media bytes.</summary>
    Task<IndexedFlvRangePlan> PlanAsync(string indexPath, long committedBytes, double startMs, double durationMs,
        CancellationToken cancellationToken = default);
}
