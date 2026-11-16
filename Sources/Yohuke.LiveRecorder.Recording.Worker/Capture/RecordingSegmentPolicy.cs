using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal static class RecordingSegmentPolicy
{
    internal static long MaximumBytes(WorkerConfiguration configuration, RecordingAssignment assignment)
    {
        var reservedPerRoom = configuration.MaxCacheBytes / Math.Max(1, configuration.MaxConcurrentEncodes);
        var budget = Math.Max(1, reservedPerRoom / 4);
        return assignment.SegmentBytes > 0 ? Math.Min(assignment.SegmentBytes, budget) : budget;
    }
}
