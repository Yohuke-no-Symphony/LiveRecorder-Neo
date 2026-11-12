using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Tests;

public sealed class SessionTimelineTests
{
    [Fact]
    public void RealTimeGapsRemainBetweenSegments()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = new BroadcastSession { StartedAtUtc = start, EndedAtUtc = start.AddMinutes(5) };
        var detail = SessionTimeline.Build(session,
        [
            new RecordingSegment { StartOffsetMs = 0, DurationMs = 60000 },
            new RecordingSegment { StartOffsetMs = 180000, DurationMs = 120000 }
        ], start.AddHours(1));
        Assert.Equal(300000, detail.DurationMs);
        var gap = Assert.Single(detail.Gaps);
        Assert.Equal(60000, gap.StartMs);
        Assert.Equal(180000, gap.EndMs);
    }

    [Fact]
    public void OverlappingSegmentsDoNotCreateFalseGaps()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start, EndedAtUtc = start.AddSeconds(20) },
        [
            new RecordingSegment { StartOffsetMs = 0, DurationMs = 12000 },
            new RecordingSegment { StartOffsetMs = 10000, DurationMs = 10000 }
        ], start);
        Assert.Empty(detail.Gaps);
        Assert.Equal(20000, detail.DurationMs);
    }

    [Fact]
    public void ExportsPreserveGapDurationAndTrimSourceOffsets()
    {
        var segments = new List<RecordingSegment>
        {
            new() { RelativePath = "one.flv", StartOffsetMs = 0, DurationMs = 10000 },
            new() { RelativePath = "two.flv", StartOffsetMs = 20000, DurationMs = 10000 }
        };
        var inputs = SessionTimeline.ExportSpans(segments, 5000, 25000, path => path);
        Assert.Equal(20000, inputs.Sum(x => x.DurationMs));
        Assert.Equal(5000, inputs[0].SourceStartMs);
        Assert.True(inputs[1].IsGap);
        Assert.Equal(10000, inputs[1].DurationMs);
    }

    [Fact]
    public void ActiveWriterTailIsPendingRatherThanARecordingOutage()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start },
            [new RecordingSegment { StartOffsetMs = 0, DurationMs = 7000, IsClosed = false }], start.AddSeconds(10));
        Assert.Equal(10000, detail.DurationMs);
        Assert.Empty(detail.Gaps);
    }

    [Fact]
    public void DisconnectedClosedWriterLeavesARealTimeGap()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start },
            [new RecordingSegment { StartOffsetMs = 0, DurationMs = 7000, IsClosed = true }], start.AddSeconds(10));
        var gap = Assert.Single(detail.Gaps);
        Assert.Equal(7000, gap.StartMs);
        Assert.Equal(10000, gap.EndMs);
    }

    [Fact]
    public void FirstConnectionWaitIsNotReportedAsADisconnect()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start }, [], start.AddSeconds(2));
        Assert.Empty(detail.Gaps);
    }
}
